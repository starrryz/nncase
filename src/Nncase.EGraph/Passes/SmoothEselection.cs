// Copyright (c) Canaan Inc. All rights reserved.
// Licensed under the Apache license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nncase.Graphs;
using Nncase.IR;

namespace Nncase.Passes;

internal static class SmoothESelection // 静态工具类，装方法/DTO
{
    // 公共入口：从 smoothe 的 selection.json 重建 BaseExpr
    public static BaseExpr ExtractWithSmoothe(EClass root, IEGraph eg, string selectionJsonPath)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        if (eg is null)
        {
            throw new ArgumentNullException(nameof(eg));
        }

        if (string.IsNullOrWhiteSpace(selectionJsonPath))
        {
            throw new ArgumentException("empty path", nameof(selectionJsonPath));
        }

        var json = File.ReadAllText(selectionJsonPath);
        var sel = JsonSerializer.Deserialize<SelectionDto>(json)
                  ?? throw new InvalidDataException("Bad selection json: null");

        // eclassId -> EClass
        var byId = eg.Classes.ToDictionary(c => c.Id);

        // 选中的 enode -> true（默认引用相等够用）
        var picks = new Dictionary<ENode, bool>();
        foreach (var c in eg.Classes)
        {
            foreach (var n in c.Nodes)
            {
                picks[n] = false;  // ★ 给每个 enode 一个缺省 false
            }
        }

        var seenEClass = new HashSet<int>();

        foreach (var key in sel.Extract)
        {
            var (ec, idx) = ParseKey(key);

            if (!byId.TryGetValue(ec, out var cls))
            {
                throw new KeyNotFoundException($"EClass {ec} not found in current egraph.");
            }

            if (!seenEClass.Add(ec))
            {
                throw new InvalidProgramException($"EClass {ec} has multiple picks in extract.");
            }

            if (idx < 0 || idx >= cls.Nodes.Count)
            {
                throw new InvalidDataException(
                        $"Bad selection json: EClass {ec} node idx {idx} out of range (valid [0..{cls.Nodes.Count - 1}]).");
            }

            picks[cls.Nodes[idx]] = true;
        }

        // 闭包校验：从 root 出发，每到一个 eclass 都必须有唯一 pick
        var start = root.Find();
        var q = new Queue<EClass>();
        var vis = new HashSet<EClass>();
        q.Enqueue(start);
        vis.Add(start);

        while (q.Count > 0)
        {
            var c = q.Dequeue();
            var chosen = c.Nodes.FirstOrDefault(n => picks.TryGetValue(n, out var v) && v);
            if (chosen is null)
            {
                throw new InvalidProgramException($"EClass {c.Id} has no pick (extract not closed).");
            }

            foreach (var ch in chosen.Children)
            {
                var canonical = ch.Find();
                if (vis.Add(canonical))
                {
                    q.Enqueue(canonical);
                }
            }
        }

        // 复用已有 Visitor 把 picks 转成 IR（DAG）
        return new SatExprBuildVisitor(picks).Visit(start);
    }

    // ---- 私有工具/DTO 放在后面以满足 StyleCop 成员顺序 ----
    private static (int Ec, int Idx) ParseKey(string k)
    {
        if (string.IsNullOrWhiteSpace(k))
        {
            throw new FormatException("Empty enode key.");
        }

        var parts = k.Split('.');
        if (parts.Length != 2)
        {
            throw new FormatException($"Bad enode key: '{k}'. Expected '<eclass>.<localIndex>'.");
        }

        return (int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    // DTO：PascalCase 属性 + JsonPropertyName 对齐你的 JSON 字段（batch 可留作将来使用）
    private sealed record SelectionDto(
        [property: JsonPropertyName("batch")] int Batch,
        [property: JsonPropertyName("extract")] string[] Extract);
}
