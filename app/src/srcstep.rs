use crate::{files, Ctx};
use anyhow::{bail, Context, Result};
use fancy_regex::{Captures, Regex as NetRegex};
use regex::Regex;
use std::fs;
use std::path::{Path, PathBuf};

const BOM: &[u8] = &[0xEF, 0xBB, 0xBF];

// Like File.ReadAllText: the byte order mark is not part of the text.
fn read_text(path: &Path) -> Result<(String, bool)> {
    let bytes = fs::read(path).with_context(|| format!("cannot read {}", path.display()))?;
    let bom = bytes.starts_with(BOM);
    let body = if bom { &bytes[BOM.len()..] } else { &bytes[..] };
    Ok((String::from_utf8_lossy(body).into_owned(), bom))
}

fn write_text(path: &Path, text: &str, bom: bool) -> Result<()> {
    let mark = if bom { BOM } else { &[] };
    files::write(path, [mark, text.as_bytes()].concat())
}

fn has_extension(path: &Path, extension: &str) -> bool {
    path.extension().is_some_and(|e| e.eq_ignore_ascii_case(extension))
}

fn is_source(path: &Path) -> bool {
    has_extension(path, "cs")
        || has_extension(path, "il")
        || path.file_name().is_some_and(|n| n.eq_ignore_ascii_case("app.manifest"))
}

fn copy_sources(from: &Path, to: &Path) -> Result<()> {
    for path in files::walk(from)?.into_iter().filter(|path| is_source(path)) {
        let target = to.join(path.strip_prefix(from)?);
        if let Some(parent) = target.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::copy(&path, &target)?;
    }
    Ok(())
}

fn csharp_files(dir: &Path) -> Result<Vec<PathBuf>> {
    Ok(files::walk(dir)?.into_iter().filter(|path| has_extension(path, "cs")).collect())
}

fn group<'t>(caps: &Captures<'t>, number: usize) -> &'t str {
    caps.get(number).map_or("", |m| m.as_str())
}

// A .NET substitution ($1, ${name}, $$, $&), with .NET's reading of it: `$12` is group 12 when
// it exists, else the longest prefix that names a group; an unknown group stays as written.
fn expand(replacement: &str, caps: &Captures, out: &mut String) {
    let mut rest = replacement;
    while let Some(at) = rest.find('$') {
        out.push_str(&rest[..at]);
        let tail = &rest[at + 1..];
        let digits = tail.bytes().take_while(u8::is_ascii_digit).count();
        let (text, used) = if tail.starts_with('$') {
            ("$", 1)
        } else if tail.starts_with('&') {
            (group(caps, 0), 1)
        } else if digits > 0 {
            let known = (1..=digits).rev().find(|&n| tail[..n].parse::<usize>().is_ok_and(|g| g < caps.len()));
            match known {
                Some(n) => (group(caps, tail[..n].parse().unwrap()), n),
                None => (&rest[at..at + 2], 1),
            }
        } else if let Some(close) = tail.strip_prefix('{').and_then(|name| name.find('}')) {
            let name = &tail[1..=close];
            let found = match name.parse::<usize>() {
                Ok(n) => caps.get(n),
                Err(_) => caps.name(name),
            };
            (found.map_or("", |m| m.as_str()), close + 2)
        } else {
            ("$", 0)
        };
        out.push_str(text);
        rest = &tail[used..];
    }
    out.push_str(rest);
}

fn replace_all(rx: &NetRegex, text: &str, replacement: &str) -> Result<(String, usize)> {
    let mut out = String::with_capacity(text.len());
    let mut last = 0;
    let mut count = 0;
    for caps in rx.captures_iter(text) {
        let caps = caps?;
        let whole = caps.get(0).unwrap();
        out.push_str(&text[last..whole.start()]);
        expand(replacement, &caps, &mut out);
        last = whole.end();
        count += 1;
    }
    out.push_str(&text[last..]);
    Ok((out, count))
}

// Newtonsoft.Json was compiled as C# 9 with nullable annotations. At C# 7.3 the decompiler
// prints them as [Nullable] / [NullableContext] attributes, which the compiler rejects (CS8623,
// CS8335), and marks their embedded types with [Embedded], a reserved name (CS8336). None of
// them changes the IL.
fn strip_nullable(ctx: &Ctx, src: &Path) -> Result<()> {
    let item = r"(?:System\.Runtime\.CompilerServices\.)?Nullable(?:Context)?(?:Attribute)?\((?:[^()]|\([^()]*\))*\)";
    let passes = [
        Regex::new(&format!(r"\[(?:\w+:\s*)?{0}(?:\s*,\s*{0})*\]\s*", item))?,
        Regex::new(&format!(r"{}\s*,\s*", item))?,
        Regex::new(&format!(r",\s*{}", item))?,
    ];
    let embedded = Regex::new(r"\[Embedded\]\s*|using Microsoft\.CodeAnalysis;\r?\n")?;
    let embedded_type = src.join("Microsoft\\CodeAnalysis\\EmbeddedAttribute.cs");
    if embedded_type.is_file() {
        fs::remove_file(&embedded_type)?;
    }
    let mut stripped = 0;
    for file in csharp_files(src)? {
        let (text, _) = read_text(&file)?;
        let has_embedded = text.contains("[Embedded]");
        if !(has_embedded || text.contains("Nullable")) {
            continue;
        }
        let mut new = text.clone();
        for pass in passes.iter().chain(has_embedded.then_some(&embedded)) {
            new = pass.replace_all(&new, "").into_owned();
        }
        if new != text {
            write_text(&file, &new, false)?;
            stripped += 1;
        }
    }
    ctx.log(format!("      nullable attributes removed from {} files", stripped));
    Ok(())
}

// docs/fixups.tsv: file<TAB>old<TAB>new[<TAB>note]. The old text must occur exactly once in the
// file; `re:` makes it a .NET regular expression (one match), `re*:` replaces every match.
struct Fixup<'a> {
    file: &'a str,
    old: &'a str,
    new: &'a str,
}

impl Fixup<'_> {
    fn parse(line: &str) -> Result<Fixup<'_>> {
        let mut fields = line.split('\t');
        match (fields.next(), fields.next(), fields.next()) {
            (Some(file), Some(old), Some(new)) if !old.is_empty() => Ok(Fixup { file, old, new }),
            _ => bail!("expected file<TAB>old<TAB>new[<TAB>note]"),
        }
    }

    fn apply(&self, text: &str) -> Result<std::result::Result<String, String>> {
        let (every, pattern) = match (self.old.strip_prefix("re*:"), self.old.strip_prefix("re:")) {
            (Some(pattern), _) => (true, pattern),
            (_, Some(pattern)) => (false, pattern),
            _ => {
                let count = text.matches(self.old).count();
                return Ok(match count {
                    1 => Ok(text.replace(self.old, self.new)),
                    _ => Err(format!("old text found {} times in {}, expected 1", count, self.file)),
                });
            }
        };
        let rx = NetRegex::new(pattern).context("bad pattern")?;
        let replacement = self.new.replace("\\n", "\r\n").replace("\\t", "\t");
        let (new, count) = replace_all(&rx, text, &replacement)?;
        Ok(if count == 1 || (every && count > 1) {
            Ok(new)
        } else {
            Err(format!("pattern found {} times in {}", count, self.file))
        })
    }
}

fn apply_fixups(ctx: &Ctx, src: &Path, lenient: bool) -> Result<()> {
    let table = ctx.path("docs\\fixups.tsv");
    let content = if table.is_file() { read_text(&table)?.0 } else { String::new() };
    let mut applied = 0;
    for (index, line) in content.lines().enumerate() {
        if line.trim().is_empty() || line.starts_with('#') {
            continue;
        }
        let at = || format!("fixups.tsv line {}", index + 1);
        let fixup = Fixup::parse(line).with_context(at)?;
        let file = src.join(fixup.file);
        if !file.is_file() {
            bail!("{}: no such file in src: {}", at(), fixup.file);
        }
        let (text, bom) = read_text(&file)?;
        match fixup.apply(&text).with_context(at)? {
            Ok(new) => {
                write_text(&file, &new, bom)?;
                applied += 1;
            }
            Err(why) if lenient => ctx.log(format!("      {}: {} (skipped)", at(), why)),
            Err(why) => bail!("{}: {}", at(), why),
        }
    }
    ctx.log(format!("      {} source fixups applied", applied));
    Ok(())
}

pub fn run(ctx: &Ctx, raw: &Path, src: &Path, lenient_fixups: bool) -> Result<()> {
    files::empty_dir(src)?;
    copy_sources(raw, src)?;
    strip_nullable(ctx, src)?;
    apply_fixups(ctx, src, lenient_fixups)?;
    ctx.log(format!("      {} C# files in {}", csharp_files(src)?.len(), src.display()));
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn substitute(pattern: &str, text: &str, replacement: &str) -> String {
        replace_all(&NetRegex::new(pattern).unwrap(), text, replacement).unwrap().0
    }

    #[test]
    fn substitutions_read_like_dotnet() {
        assert_eq!(substitute("(a)(b)", "ab", "$2$1"), "ba");
        assert_eq!(substitute("(a)(b)", "ab", "$12"), "a2");
        assert_eq!(substitute("(a)(b)", "ab", "$3|$&|$$|$"), "$3|ab|$|$");
        assert_eq!(substitute("(?<x>a)(b)", "ab", "${x}${2}${y}${"), "ab${");
        assert_eq!(substitute("a", "a", "$x"), "$x");
    }

    #[test]
    fn fixups_need_the_expected_number_of_matches() {
        let fix = |old, text: &str| Fixup { file: "f.cs", old, new: "x" }.apply(text).unwrap();
        assert_eq!(fix("a", "bab"), Ok("bxb".to_string()));
        assert!(fix("a", "aa").is_err());
        assert!(fix("re:a", "aa").is_err());
        assert_eq!(fix("re*:a", "aa"), Ok("xx".to_string()));
        assert!(fix("re*:a", "b").is_err());
    }
}
