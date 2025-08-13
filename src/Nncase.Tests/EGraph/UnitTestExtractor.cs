// Copyright (c) Canaan Inc. All rights reserved.
// Licensed under the Apache license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.OrTools.Sat;
using Nncase.CostModel;
using Nncase.IR;
using Nncase.IR.Math;
using Nncase.Passes;
using Nncase.PatternMatch;
using Nncase.Tests.TestFixture;
using Xunit;
using static Nncase.IR.F.Math;
using static Nncase.PatternMatch.F.Math;
using static Nncase.PatternMatch.F.Tensors;
using static Nncase.PatternMatch.Utility;

namespace Nncase.Tests.EGraphTest;

[AutoSetupTestMethod(InitSession = true)]

public class UnitTestExtractor : TestClassBase
{
    public UnitTestExtractor()
    {
        CompileOptions.DumpFlags = Diagnostics.DumpFlags.EGraphCost | Diagnostics.DumpFlags.Rewrite | Diagnostics.DumpFlags.Compile | Diagnostics.DumpFlags.ImportOps | Diagnostics.DumpFlags.PassIR;
    }

    [Fact]
    public void Test_for_Extractor_dump()
    {
        var egraph = new EGraph();
        var a = new Var(TensorType.Scalar(DataTypes.Float64)); // var a_1 = new Var(TensorType.Scalar(DataTypes.UInt32));
        Expr b = a * 2.0; // Expr c = a_1 << 1;

        var aid = egraph.Add(a);
        var bId = egraph.Add(b); // var cId = egraph.Add(c);

        // did or eid 会变成最后的一个root
        var root = egraph.Add(b / 2.0); // var eId = egraph.Add(c / 2);
        EGraphPrinter.DumpEgraphAsDot(egraph, "exampleMerge8.dot");

        // 第二步（2）：基于rule直接对图进行重写，这里是a * b / c = a * (b / c) , x / x = 1
        _ = CompilerServices.ERewrite(egraph, new IRewriteRule[] { new Passes.Rules.Arithmetic.ReassociateDiv(), new Passes.Rules.Arithmetic.XDivX() }, new());

        // todo : define pattern and rewrite  ||   x * 1 = x  || use pattern to settle
        var pattern = PatternMatch.F.Math.Mul(IsWildcard("lhs"), IsConst(1.0d));

        if (CompilerServices.TryMatchRoot(root.Nodes, pattern, out var eResults))
        {
            egraph.Union(aid, root);
            egraph.Rebuild();
        }

        // egraph_changed.Rebuild(); not the problem && egraph will be modified not only egraph_changed
        EGraphPrinter.DumpEgraphAsDot(egraph, "exampleMerge9_0.dot");

        // created by evaluator
        var evaluator = new EGraphCostEvaluator(root.Find(), CompileOptions, null);
        var costmodel = evaluator.Evaluate();

        // cost/pick.dot是由extrator生成的
        var extractor = new EGraphExtractor(costmodel);
        EGraphExtractConstrains[]? constrains = null;
        var selected = extractor.Extract(root.Find(), egraph, constrains ?? Array.Empty<EGraphExtractConstrains>());
        CompilerServices.DumpIR(selected, string.Empty, Dumpper.Directory);

        // 或者更加集成的方法
        // var res = EGraphExtensions.Extract(egraph, root.Find(), CompileOptions, null, constrains);
    }

    [Fact]
    public void Test_dump_json_for_smoothe()
    {
        var egraph = new EGraph();
        var a = new Var(TensorType.Scalar(DataTypes.Float64)); // var a_1 = new Var(TensorType.Scalar(DataTypes.UInt32));
        Expr b = a * 2.0; // Expr c = a_1 << 1; 左移是针对int的操作，但是除以是float的操作，融合不到一块

        var aid = egraph.Add(a);
        var bId = egraph.Add(b); // var cId = egraph.Add(c);

        // did or eid 会变成最后的一个root
        var root = egraph.Add(b / 2.0); // var eId = egraph.Add(c / 2);

        // 第二步（2）：基于rule直接对图进行重写，这里是a * b / c = a * (b / c) , x / x = 1
        _ = CompilerServices.ERewrite(egraph, new IRewriteRule[] { new Passes.Rules.Arithmetic.ReassociateDiv(), new Passes.Rules.Arithmetic.XDivX() }, new());

        // todo : define pattern and rewrite  ||   x * 1 = x  || use pattern to settle
        var pattern = PatternMatch.F.Math.Mul(IsWildcard("lhs"), IsConst(1.0d));

        if (CompilerServices.TryMatchRoot(root.Nodes, pattern, out var eResults))
        {
            egraph.Union(aid, root);
            egraph.Rebuild();
        }

        EGraphPrinter.DumpEgraphAsDot(egraph, "exampleMerge9_2.dot");
        var evaluator = new EGraphCostEvaluator(root.Find(), CompileOptions, null);
        var costmodel = evaluator.Evaluate();

        // 以上都是剽窃自上一个测试的图
        // var allClasses = egraph.Classes.ToList();
        var rootClass = root.Find();
        var reachableClasses = CollectReachableClasses(rootClass);
        var allClasses = reachableClasses.ToList();

        // 选每个 EClass 的“代表 ENode”——若你有真实 cost，可改成选 cost 最小的
        ENode PickRepr(EClass cls) => cls.Nodes[0];
        var reprOfEClass = allClasses.ToDictionary(cls => cls, cls => PickRepr(cls));

        var localIndex = new Dictionary<EClass, Dictionary<ENode, int>>();
        foreach (var cls in allClasses.OrderBy(c => c.Id))
        {
            int i = 0;
            var dict = new Dictionary<ENode, int>();
            foreach (var node in cls.Nodes)
            {
                dict[node] = i++;
            }

            localIndex[cls] = dict;
        }

        // ✅ NodeKey：<eclass>.<local_index_in_that_eclass>
        string NodeKey(ENode n, EClass owner) => $"{owner.Id}.{localIndex[owner][n]}";

        // 构造 { "nodes": { "<id>.0": {op, children, eclass, cost}, ... } }
        var nodesDict = new Dictionary<string, object>();

        foreach (var cls in allClasses)
        {
            foreach (var node in cls.Nodes)
            {
                var key = NodeKey(node, cls);

                // children: EClass -> 代表 ENode -> key
                var childKeys = new List<string>();
                var childEClassIds = new List<int>();
                foreach (var childCls in node.Children)
                {
                    var cc = childCls.Find(); // 防止 union 后引用未归一
                    if (!reachableClasses.Contains(cc))
                    {
                        // 保险起见多走一步，其实cls in allClasses已经确定这是个闭包
                        continue;
                    }

                    var childRepr = reprOfEClass[cc];
                    childKeys.Add(NodeKey(childRepr, cc));
                    childEClassIds.Add(cc.Id);
                }

                nodesDict[key] = new
                {
                    op = GetOpName(node),
                    op_detail = BuildOpDetail(node, cls, childEClassIds),
                    children = childKeys,
                    eclass = cls.Id.ToString(),
                    cost = GetCostFallback(node, costmodel), // TODO: 有真实 cost 就改这里
                };
            }
        }

        // 打包 & 写文件
        var rootEclasses = new List<string> { rootClass.Id.ToString() };

        var flexRoot = new
        {
            root_eclasses = rootEclasses,   // 新增字段
            nodes = nodesDict,
        };
        var opts = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        File.WriteAllText("egraph_flex_dump.json", JsonSerializer.Serialize(flexRoot, opts));

        var selectionPath = Path.Combine(Environment.CurrentDirectory, "selection_newly_08_11.json");
        if (!File.Exists(selectionPath))
        {
            throw new FileNotFoundException($"selection json not found: {selectionPath}");
        }

        // 基于“当前内存里的 egraph + rootClass”，用 selection.json 复原一棵（实际是 DAG）BaseExpr
        var extracted = SmoothESelection.ExtractWithSmoothe(rootClass, egraph, selectionPath);

        // （可选）把复原出来的 BaseExpr 放进一个新的 egraph 打一份 dot 看看
        var egAfter = new EGraph();
        var newRoot = egAfter.Add(extracted);
        EGraphPrinter.DumpEgraphAsDot(egAfter, "smoothe_extract.dot");

        // ====== 辅助函数 ======
        static string DescribeOp(Op o)
        {
            // 先按类名特殊处理常见情况，保证可读性
            var typeName = o.GetType().Name;

            // 例如 "Binary" 有 BinaryOp 属性
            var prop =
                o.GetType().GetProperty("BinaryOp") ??
                o.GetType().GetProperty("UnaryOp") ??
                o.GetType().GetProperty("ReduceOp") ??
                o.GetType().GetProperty("CompareOp") ??

                // 一些实现会用 Kind 命名
                o.GetType().GetProperty("Kind");

            if (prop != null)
            {
                var val = prop.GetValue(o);
                if (val != null)
                {
                    return $"{typeName}({val})";
                }
            }

            // 兜底：没有这些属性就只返回类型名
            return typeName;
        }

        static HashSet<EClass> CollectReachableClasses(EClass rootClass)
        {
            var q = new Queue<EClass>();
            var vis = new HashSet<EClass>();

            q.Enqueue(rootClass);
            vis.Add(rootClass);

            while (q.Count > 0)
            {
                var cls = q.Dequeue();
                foreach (var node in cls.Nodes)
                {
                    foreach (var child in node.Children)
                    {
                        var cc = child.Find();
                        if (vis.Add(cc))
                        {
                            q.Enqueue(cc);
                        }
                    }
                }
            }

            return vis;
        }

        // 尽量拿“好看”的算子名；按你工程里 ENode 的实际结构调整即可
        static string GetOpName(ENode node)
        {
            try
            {
                // 1) 优先尝试 node.Op / node.Operator 的 Name
                var opProp = node.GetType().GetProperty("Op") ?? node.GetType().GetProperty("Operator");
                if (opProp != null)
                {
                    var opVal = opProp.GetValue(node);
                    if (opVal != null)
                    {
                        var nameProp = opVal.GetType().GetProperty("Name");
                        if (nameProp != null)
                        {
                            var s = nameProp.GetValue(opVal)?.ToString();
                            if (!string.IsNullOrEmpty(s))
                            {
                                return s!;
                            }
                        }

                        return opVal.GetType().Name;
                    }
                }

                // 2) 退而求其次：看是否有 IR Expr（Call/Const/Tuple…）
                var exprProp = node.GetType().GetProperty("Expr");
                if (exprProp != null)
                {
                    var expr = exprProp.GetValue(node);
                    if (expr is Call call)
                    {
                        if (call.Target is Op o)
                        {
                            return o.GetType().Name;
                        }

                        return "call";
                    }

                    if (expr is Const)
                    {
                        return "const";
                    }
                    else
                    {
                        return expr?.GetType().Name ?? node.GetType().Name;
                    }
                }

                // 3) 实在不行：类型名兜底
                return node.GetType().Name;
            }
            catch
            {
                return "unknown";
            }
        }

        // 没有真实 cost 时的兜底：常量=0，其它=1
        static double GetCostFallback(ENode node, EGraphCostModel costmodel)
        {
            // 优先用 evaluator 的结果：取 CPUCycles（你也可以换成自己的主因子）
            if (costmodel.TryGet(node, out var cost))
            {
                try
                {
                    // 主因子：CPUCycles（如果你希望换成别的就改这里）
                    return (double)cost[CostFactorNames.CPUCycles];
                }
                catch
                {
                    // 如果没有这个因子，就先给 0（或你想要的默认值）
                    return 0.0;
                }
            }

            // 拿不到就用最简单兜底：常量=0；否则有子节点=1、无子节点=0
            return (node.Expr is Const) ? 0.0 : (node.Children.Any() ? 1.0 : 0.0);
        }

        static string BuildOpDetail(ENode node, EClass cls, List<int> childEClassIds)
        {
            // enode 地址（区分 union 前后是否是同一个对象）
            var enodeAddr = RuntimeHelpers.GetHashCode(node);

            // Expr 基本类型 & 可读签名
            string exprSig = node.Expr switch
            {
                Call call => BuildCallSig(call),     // Call(Binary(Mul)) 等
                Const c => BuildConstSig(c),
                Var v => BuildVarSig(v),
                Op o => DescribeOp(o),          // ★ 只有是 Op 才会调用，不会为 null
                Expr e => e.GetType().Name,       // 其它 IR 节点兜底
                null => "null",
                _ => throw new NotImplementedException(),
            };

            // 节点自身已推断的类型（CheckedType）
            string ty = SafeType(node);

            // children 的 eclass id 列表（Find() 之后的归一 id）
            string childs = childEClassIds.Count > 0 ? $"[{string.Join(',', childEClassIds)}]" : "[]";

            return $"{exprSig}|eclass={cls.Id}|enode=0x{enodeAddr:x}|children_eclass={childs}|type={ty}";
        }

        static string BuildCallSig(Call? call)
        {
            if (call == null)
            {
                return "Call(?)";
            }

            if (call.Target is Op o)
            {
                // 原来是 o.GetType().Name；改为带子枚举的可读格式
                return $"Call({DescribeOp(o)})";
            }

            return "Call(Fn)";
        }

        static string BuildConstSig(Const? c)
        {
            if (c == null)
            {
                return "Const(?)";
            }

            try
            {
                var t = c.CheckedType as TensorType;
                var shp = t?.Shape.ToString() ?? "?";
                var dt = t?.DType.ToString() ?? "?";
                return $"Const({dt},{shp})";
            }
            catch
            {
                return "Const";
            }
        }

        static string BuildVarSig(Var? v)
        {
            if (v == null)
            {
                return "Var(?)";
            }

            try
            {
                var name = v.Name ?? "_";
                var t = v.CheckedType as TensorType;
                return $"Var({name},{t?.DType},{t?.Shape})";
            }
            catch
            {
                return "Var";
            }
        }

        static string SafeType(ENode node)
        {
            try
            {
                return node.GetType().GetProperty("CheckedType")?.GetValue(node)?.ToString()
                    ?? (node.Expr as Expr)?.CheckedType?.ToString()
                    ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
