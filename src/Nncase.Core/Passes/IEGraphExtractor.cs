// Copyright (c) Canaan Inc. All rights reserved.
// Licensed under the Apache license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.OrTools.Sat;
using Nncase.IR;

namespace Nncase.Passes;

public delegate void EGraphExtractConstrains(CpModel model, IReadOnlyDictionary<ENode, BoolVar> vars);

/// <summary>
/// EGraph extractor interface.
/// </summary>
public interface IEGraphExtractor
{
    /// <summary>
    /// Extract an expression from an EGraph.
    /// </summary>
    /// <param name="root">Root EClass.</param>
    /// <param name="eGraph">EGraph instance.</param>
    /// <param name="constrains">111.</param>
    /// <returns>Extracted expression.</returns>
    // Due to the use of constrains of current extractor, we keep it as less change as possible.
    BaseExpr Extract(EClass root, IEGraph eGraph, EGraphExtractConstrains[] constrains);
}
