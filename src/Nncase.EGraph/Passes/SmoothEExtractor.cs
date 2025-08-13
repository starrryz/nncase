// Copyright (c) Canaan Inc. All rights reserved.
// Licensed under the Apache license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nncase.CostModel;
using Nncase.Diagnostics;
using Nncase.IR;

namespace Nncase.Passes;

/// <summary>
/// SmoothE extractor: dumps a flexible e-graph json, reads a selection json,
/// and reconstructs a <see cref="BaseExpr"/> as the extraction result.
/// </summary>
internal sealed class SmoothEExtractor : IEGraphExtractor
{
    private EGraphCostModel? _costModel;
    private CompileOptions _compileOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmoothEExtractor"/> class.
    /// </summary>
    public SmoothEExtractor(CompileOptions compileOptions)
    {
        _costModel = null; // 先不赋具体值
        _compileOptions = compileOptions;
    }

    /// <inheritdoc/>
    public BaseExpr Extract(EClass root, IEGraph eGraph, EGraphExtractConstrains[] constrains)
    {
        // 1) 规范 root
        var rootClass = root.Find();
        var evaluator = new EGraphCostEvaluator(root.Find(), _compileOptions, null);
        _costModel = evaluator.Evaluate();

        // 2) 收集可达 EClass
        var reachableClasses = CollectReachableClasses(rootClass);
        var allClasses = reachableClasses.ToList();

        // 3) 为每个 EClass 选择“代表 ENode”（这里默认取第一个；你可替换为最小 cost）
        ENode PickRepr(EClass cls) => cls.Nodes[0];
        var reprOfEClass = allClasses.ToDictionary(cls => cls, PickRepr);

        // 4) 为每个 EClass 的 enode 建立“局部序号”
        var localIndex = new Dictionary<EClass, Dictionary<ENode, int>>();
        foreach (var cls in allClasses.OrderBy(c => c.Id))
        {
            var dict = new Dictionary<ENode, int>();
            int i = 0;
            foreach (var node in cls.Nodes)
            {
                dict[node] = i++;
            }

            localIndex[cls] = dict;
        }

        // NodeKey: "<eclass>.<local_index_in_that_eclass>"
        string NodeKey(ENode n, EClass owner) => $"{owner.Id}.{localIndex[owner][n]}";

        // 5) 构造 nodes 字典
        var nodesDict = new Dictionary<string, object>();
        foreach (var cls in allClasses)
        {
            foreach (var node in cls.Nodes)
            {
                var key = NodeKey(node, cls);

                // children：EClass -> 代表 ENode -> key
                var childKeys = new List<string>();
                var childEClassIds = new List<int>();

                foreach (var childCls in node.Children)
                {
                    var cc = childCls.Find(); // 避免 union 后未归一
                    if (!reachableClasses.Contains(cc))
                    {
                        // 理论上 allClasses 已经保证闭包；这里只是保守防护
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
                    cost = GetCostFallback(node, _costModel),
                };
            }
        }

        // 6) 落盘 egraph_flex_dump.json
        var rootEclasses = new List<string> { rootClass.Id.ToString() };
        var flexRoot = new
        {
            root_eclasses = rootEclasses,
            nodes = nodesDict,
        };

        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        File.WriteAllText("egraph_flex_dump.json", JsonSerializer.Serialize(flexRoot, opts));

        // 从这开始是需要添加调用子进程的

        // 7) 读取 selection json，并按选择复原表达式
        var selectionPath = Path.Combine(Environment.CurrentDirectory, "selection_newly_08_11.json");
        if (!File.Exists(selectionPath))
        {
            throw new FileNotFoundException($"selection json not found: {selectionPath}");
        }

        // 说明：SmoothESelection 为你已有的重建逻辑工具类
        var extracted = SmoothESelection.ExtractWithSmoothe(rootClass, eGraph, selectionPath);

        // 8) （可选）把复原出来的表达式放入新 egraph 并导出 dot，便于目视检查
        var egAfter = new EGraph();
        _ = egAfter.Add(extracted);
        EGraphPrinter.DumpEgraphAsDot(egAfter, "smoothe_extract.dot");
        return extracted;
    }

    // ====== 辅助函数 ======
    private static HashSet<EClass> CollectReachableClasses(EClass rootClass)
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

    private static string GetOpName(ENode node)
    {
        try
        {
            // 1) 优先取 node.Op / node.Operator 的 Name
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

            // 2) 尝试 IR Expr（Call/Const/Var…）
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

                return expr?.GetType().Name ?? node.GetType().Name;
            }

            // 3) 兜底
            return node.GetType().Name;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string DescribeOp(Op o)
    {
        var typeName = o.GetType().Name;
        var prop =
            o.GetType().GetProperty("BinaryOp") ??
            o.GetType().GetProperty("UnaryOp") ??
            o.GetType().GetProperty("ReduceOp") ??
            o.GetType().GetProperty("CompareOp") ??
            o.GetType().GetProperty("Kind");

        if (prop != null)
        {
            var val = prop.GetValue(o);
            if (val != null)
            {
                return $"{typeName}({val})";
            }
        }

        return typeName;
    }

    private static string BuildOpDetail(ENode node, EClass cls, List<int> childEClassIds)
    {
        var enodeAddr = RuntimeHelpers.GetHashCode(node);

        string exprSig = node.Expr switch
        {
            Call call => BuildCallSig(call),
            Const c => BuildConstSig(c),
            Var v => BuildVarSig(v),
            Op o => DescribeOp(o),
            Expr e => e.GetType().Name,
            null => "null",
            _ => "unknown",
        };

        string ty = SafeType(node);
        string childs = childEClassIds.Count > 0 ? $"[{string.Join(',', childEClassIds)}]" : "[]";
        return $"{exprSig}|eclass={cls.Id}|enode=0x{enodeAddr:x}|children_eclass={childs}|type={ty}";
    }

    private static string BuildCallSig(Call? call)
    {
        if (call == null)
        {
            return "Call(?)";
        }

        if (call.Target is Op o)
        {
            return $"Call({DescribeOp(o)})";
        }

        return "Call(Fn)";
    }

    private static string BuildConstSig(Const? c)
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

    private static string BuildVarSig(Var? v)
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

    private static string SafeType(ENode node)
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

    private static double GetCostFallback(ENode node, EGraphCostModel costModel)
    {
        if (costModel.TryGet(node, out var cost))
        {
            try
            {
                return (double)cost[CostFactorNames.CPUCycles];
            }
            catch
            {
                return 0.0;
            }
        }

        return (node.Expr is Const) ? 0.0 : (node.Children.Any() ? 1.0 : 0.0);
    }
}
