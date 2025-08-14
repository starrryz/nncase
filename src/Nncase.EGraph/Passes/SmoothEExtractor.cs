// Copyright (c) Canaan Inc. All rights reserved.
// Licensed under the Apache license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
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

        // 6) 落盘 egraph_flex_dump.json，生成输入json文件
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

        // 将输出的json文件放入更新在外部目录，方便共享
        var sharedRootDir = "/compiler/external_shared";
        var smootheDatasetDir = "/compiler/yaohuicai-smoothe-artifact-f98add8/dataset/data_from_nncase";
        var sharedInputDir = Path.Combine(sharedRootDir, "input");
        var sharedOutputDir = Path.Combine(sharedRootDir, "output"); // 预留：后续 smoothe 输出可统一指向这里
        var nncaseTestsBinDir = "/compiler/nncase/src/Nncase.Tests/bin/Release/net8.0";

        // 生成时间戳前缀，例如 0814_14_52
        string timestamp = DateTime.Now.ToString("MMdd_HH_mm");

        // 用时间戳生成两个文件名
        var dumpFileName = $"{timestamp}_dump.json";
        var selectionFileName = $"{timestamp}_selection.json";

        var egraphDumpPath = Path.Combine(sharedInputDir, dumpFileName);
        var egraphDumpPath2 = Path.Combine(smootheDatasetDir, dumpFileName);
        File.WriteAllText(egraphDumpPath, JsonSerializer.Serialize(flexRoot, opts));
        File.WriteAllText(egraphDumpPath2, JsonSerializer.Serialize(flexRoot, opts));

        // === 7) 调用子进程运行 smoothe（工作目录保持为 smoothe 仓库） ===
        var smootheRepoDir = "/compiler/yaohuicai-smoothe-artifact-f98add8";
        var condaExe = "/opt/conda/condabin/conda";
        var args = "run -n smoothe-env python launch.py --acyclic --dataset data_from_nncase --method smoothe --repeat 1 --greedy_ini";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = condaExe,
            Arguments = args,
            WorkingDirectory = smootheRepoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start smoothe subprocess (conda run).");
        string stdOut = proc.StandardOutput.ReadToEnd();
        string stdErr = proc.StandardError.ReadToEnd();

        // 硬超时（按需调整）
        if (!proc.WaitForExit((int)TimeSpan.FromMinutes(15).TotalMilliseconds))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            { /* ignored */
            }

            throw new TimeoutException("smoothe subprocess timeout.");
        }

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"smoothe subprocess failed with exit code {proc.ExitCode}.\n[stdout]\n{stdOut}\n[stderr]\n{stdErr}");
        }

        // 新建目录，如已存在相当于做一次检查，
        Directory.CreateDirectory(nncaseTestsBinDir);

        // 候选位置：优先 external_shared/output，然后 smoothe 仓库内常见输出处；否则模糊查找
        string[] candidateSelectionPaths =
        {
            Path.Combine(smootheRepoDir, "logs", "smoothe_log", selectionFileName),
            Path.Combine(sharedOutputDir, selectionFileName),                          // 共享输出（以后 smoothe 可切到这里）
            Path.Combine(smootheRepoDir, selectionFileName),
            Path.Combine(smootheRepoDir, "output", selectionFileName),
        };

        // 这里的赋值是xx.json结尾
        string? foundSelection = candidateSelectionPaths.FirstOrDefault(File.Exists);

        // rescued by recursive search the whole dir
        if (foundSelection is null)
        {
            var cand = new DirectoryInfo(smootheRepoDir)
                .EnumerateFiles("*_selection.json", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (cand is not null)
            {
                foundSelection = cand.FullName;
            }
        }

        // no method is valid, throw error
        if (foundSelection is null || !File.Exists(foundSelection))
        {
            throw new FileNotFoundException(
                $"Cannot locate selection json produced by smoothe.\n" +
                $"Tried: {string.Join(", ", candidateSelectionPaths)}\n" +
                $"WorkingDirectory: {smootheRepoDir}\n[stdout]\n{stdOut}\n[stderr]\n{stdErr}");
        }

        // if find, copy this into where can be used by nncase
        var selectionPath = Path.Combine(nncaseTestsBinDir, selectionFileName);
        var externalOutputPath = Path.Combine(sharedOutputDir, selectionFileName);

        // first copy and then check
        File.Copy(foundSelection, selectionPath, overwrite: true);
        File.Copy(foundSelection, externalOutputPath, overwrite: true);
        if (!File.Exists(selectionPath) || !File.Exists(externalOutputPath))
        {
            throw new FileNotFoundException($"selection json not found: {selectionPath}");
        }

        // and finally back here and use it
        var extracted = SmoothESelection.ExtractWithSmoothe(rootClass, eGraph, selectionPath);

        // (optional) intend to verify it by dump dot
        try
        {
            var egAfter = new EGraph();
            _ = egAfter.Add(extracted);
            EGraphPrinter.DumpEgraphAsDot(egAfter, "smoothe_extract.dot");
        }
        catch
        {
        }

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
