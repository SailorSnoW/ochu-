// Finds property setters the obfuscator left without their name, by how they are called.
//
// 'F(new T { P = a })' compiles to
//
//     <operands of F> ; newobj T::.ctor ; dup ; a ; callvirt T::set_P ; call F
//
// and no other source does: with a local ('T t = new T(); t.M(a); F(x, t);') the object is
// created before the operands of F are pushed. A void instance method with one parameter,
// called on the 'dup' of a 'newobj' while something else is already on the stack, is
// therefore a setter. Named so again, it lets the decompiler write the initializer.
//
// Compiled by scripts/apply-names.ps1 with Add-Type (Windows PowerShell 5.1: C# 5 only).
using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

public static class AccessorUse
{
    // Stack depth before each instruction; -1 where a straight reading cannot tell.
    static int[] Depths(MethodDef m)
    {
        var ins = m.Body.Instructions;
        var depth = new int[ins.Count];
        for (int i = 0; i < depth.Length; i++) depth[i] = -1;
        var index = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) index[ins[i]] = i;
        foreach (var eh in m.Body.ExceptionHandlers)
        {
            int at;
            if (eh.TryStart != null && index.TryGetValue(eh.TryStart, out at)) depth[at] = 0;
            if (eh.HandlerStart != null && index.TryGetValue(eh.HandlerStart, out at))
                depth[at] = eh.HandlerType == ExceptionHandlerType.Catch || eh.HandlerType == ExceptionHandlerType.Filter ? 1 : 0;
            if (eh.FilterStart != null && index.TryGetValue(eh.FilterStart, out at)) depth[at] = 1;
        }
        int cur = 0;
        for (int i = 0; i < ins.Count; i++)
        {
            if (depth[i] >= 0) cur = depth[i];
            else if (cur < 0) cur = 0;
            depth[i] = cur;
            int pushes, pops;
            ins[i].CalculateStackUsage(m.HasReturnType, out pushes, out pops);
            if (pops < 0) cur = 0; else cur = cur - pops + pushes;
            var one = ins[i].Operand as Instruction;
            var many = ins[i].Operand as IList<Instruction>;
            int t;
            if (one != null && index.TryGetValue(one, out t) && depth[t] < 0) depth[t] = cur;
            if (many != null) foreach (var x in many) if (x != null && index.TryGetValue(x, out t) && depth[t] < 0) depth[t] = cur;
            switch (ins[i].OpCode.FlowControl)
            {
                case FlowControl.Branch: case FlowControl.Return: case FlowControl.Throw: cur = -1; break;
            }
        }
        return depth;
    }

    static bool Candidate(MethodDef d)
    {
        if (d == null || d.IsStatic || d.IsConstructor || d.HasGenericParameters) return false;
        if (d.MethodSig.Params.Count != 1 || d.MethodSig.RetType.ElementType != ElementType.Void) return false;
        string n = d.Name;
        return !n.StartsWith("set_") && !n.StartsWith("get_") && !n.StartsWith("add_") && !n.StartsWith("remove_");
    }

    // Index of the instruction that pushed the receiver of the call at k, or -1.
    static int Receiver(MethodDef m, int k)
    {
        var ins = m.Body.Instructions;
        int need = 2;
        for (int j = k - 1; j >= 0; j--)
        {
            var i = ins[j];
            if (i.OpCode.FlowControl != FlowControl.Next && i.OpCode.FlowControl != FlowControl.Call) return -1;
            int pushes, pops;
            i.CalculateStackUsage(m.HasReturnType, out pushes, out pops);
            if (pops < 0) return -1;
            need -= pushes;
            if (need <= 0) return j;
            need += pops;
        }
        return -1;
    }

    // Getter and setter of one property, known by a compound assignment: 'x.P |= 8' compiles to
    //
    //     x ; dup ; callvirt T::get_P ; ldc.i4.8 ; or ; callvirt T::set_P
    //
    // The receiver of the second call is the 'dup' the first one reads. Written with two
    // methods ('x.Set(x.Get() | 8)') the receiver is evaluated twice. Returns getter, setter,
    // getter, setter...
    public static List<MethodDef> FindPairs(ModuleDef mod)
    {
        var found = new List<MethodDef>();
        var seen = new HashSet<MethodDef>();
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                var ins = m.Body.Instructions;
                for (int k = 0; k < ins.Count; k++)
                {
                    if (ins[k].OpCode.Code != Code.Callvirt && ins[k].OpCode.Code != Code.Call) continue;
                    var callee = ins[k].Operand as IMethod;
                    if (callee == null || callee.MethodSig == null || !callee.MethodSig.HasThis) continue;
                    if (callee.MethodSig.Params.Count != 1 || callee.MethodSig.RetType.ElementType != ElementType.Void) continue;
                    int r = Receiver(m, k);
                    if (r < 1 || r + 1 >= k || ins[r].OpCode.Code != Code.Dup) continue;
                    if (ins[r + 1].OpCode.Code != Code.Callvirt && ins[r + 1].OpCode.Code != Code.Call) continue;
                    var first = ins[r + 1].Operand as IMethod;
                    if (first == null || first.MethodSig == null || !first.MethodSig.HasThis || first.MethodSig.Params.Count != 0) continue;
                    var setter = callee.ResolveMethodDef();
                    var getter = first.ResolveMethodDef();
                    if (setter == null || getter == null || setter.Module != mod || getter.Module != mod || !Candidate(setter)) continue;
                    if (getter.DeclaringType != setter.DeclaringType || getter.IsStatic || getter.HasGenericParameters) continue;
                    string n = getter.Name;
                    if (n.StartsWith("set_") || n.StartsWith("get_") || n.StartsWith("add_") || n.StartsWith("remove_")) continue;
                    if (getter.MethodSig.RetType.FullName != setter.MethodSig.Params[0].FullName) continue;
                    if (seen.Contains(getter) || seen.Contains(setter)) continue;
                    seen.Add(getter); seen.Add(setter);
                    found.Add(getter); found.Add(setter);
                }
            }
        return found;
    }

    public static List<MethodDef> Find(ModuleDef mod)
    {
        var found = new List<MethodDef>();
        var seen = new HashSet<MethodDef>();
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                var ins = m.Body.Instructions;
                int[] depth = null;
                HashSet<Instruction> targets = null;
                // setter calls already recognised in this body, by index: a chain goes on after them
                var chained = new HashSet<int>();
                for (int k = 0; k < ins.Count; k++)
                {
                    // A field of the initializer ('dup ; a ; stfld f'): the chain goes on after it too.
                    if (ins[k].OpCode.Code == Code.Stfld)
                    {
                        int o = Receiver(m, k);
                        if (o >= 1 && ins[o].OpCode.Code == Code.Dup && (ins[o - 1].OpCode.Code == Code.Newobj || chained.Contains(o - 1))) chained.Add(k);
                        continue;
                    }
                    if (ins[k].OpCode.Code != Code.Callvirt && ins[k].OpCode.Code != Code.Call) continue;
                    var callee = ins[k].Operand as IMethod;
                    if (callee == null || callee.MethodSig == null || !callee.MethodSig.HasThis) continue;
                    if (callee.MethodSig.Params.Count != 1 || callee.MethodSig.RetType.ElementType != ElementType.Void) continue;
                    int r = Receiver(m, k);
                    if (r < 1 || ins[r].OpCode.Code != Code.Dup) continue;
                    bool onNew = ins[r - 1].OpCode.Code == Code.Newobj || chained.Contains(r - 1);
                    if (!onNew) continue;
                    if (depth == null) depth = Depths(m);
                    // A type marked beforefieldinit has no static constructor in its source: its
                    // '.cctor' is made of field initializers, where a method cannot be called on a
                    // new object as a statement. 'static T x = new T { P = a };' is the only source
                    // of 'newobj ; dup ; a ; callvirt M ; stsfld' there.
                    bool initializer = m.IsStaticConstructor && m.DeclaringType.IsBeforeFieldInit;
                    // after the dup: what was there, the object, its copy
                    if (depth[r] < 2 && !initializer)
                    {
                        // Or the object is the value of one branch of a conditional expression
                        // ('c ? new T { P = a } : null'): it is still on the stack where the
                        // branches meet, and a statement leaves nothing there.
                        if (targets == null)
                        {
                            targets = new HashSet<Instruction>();
                            foreach (var x in ins)
                            {
                                var one = x.Operand as Instruction;
                                if (one != null) targets.Add(one);
                            }
                        }
                        bool merges = k + 1 < ins.Count && depth[k + 1] >= 1
                            && (targets.Contains(ins[k + 1]) || ins[k + 1].OpCode.Code == Code.Br || ins[k + 1].OpCode.Code == Code.Br_S);
                        if (!merges) continue;
                    }
                    chained.Add(k);
                    var def = callee.ResolveMethodDef();
                    if (def == null || def.Module != mod || !Candidate(def)) continue;
                    if (seen.Add(def)) found.Add(def);
                }
            }
        return found;
    }
}
