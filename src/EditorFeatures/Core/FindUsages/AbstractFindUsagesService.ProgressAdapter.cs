﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.FindUsages;
using Microsoft.CodeAnalysis.Navigation;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.FindUsages
{
    internal abstract partial class AbstractFindUsagesService
    {
        /// <summary>
        /// Forwards <see cref="IStreamingFindLiteralReferencesProgress"/> calls to an
        /// <see cref="IFindUsagesContext"/> instance.
        /// </summary>
        private class FindLiteralsProgressAdapter : IStreamingFindLiteralReferencesProgress
        {
            private readonly IFindUsagesContext _context;
            private readonly DefinitionItem _definition;
            private readonly ClassificationOptions _classificationOptions;

            public IStreamingProgressTracker ProgressTracker
                => _context.ProgressTracker;

            public FindLiteralsProgressAdapter(
                IFindUsagesContext context, DefinitionItem definition, ClassificationOptions classificationOptions)
            {
                _context = context;
                _definition = definition;
                _classificationOptions = classificationOptions;
            }

            public async ValueTask OnReferenceFoundAsync(Document document, TextSpan span, CancellationToken cancellationToken)
            {
                var documentSpan = await ClassifiedSpansAndHighlightSpanFactory.GetClassifiedDocumentSpanAsync(
                    document, span, _classificationOptions, cancellationToken).ConfigureAwait(false);
                await _context.OnReferenceFoundAsync(
                    new SourceReferenceItem(_definition, documentSpan, SymbolUsageInfo.None), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Forwards IFindReferencesProgress calls to an IFindUsagesContext instance.
        /// </summary>
        private class FindReferencesProgressAdapter : IStreamingFindReferencesProgress
        {
            private readonly Solution _solution;
            private readonly IFindUsagesContext _context;
            private readonly FindReferencesSearchOptions _options;

            /// <summary>
            /// We will hear about definition symbols many times while performing FAR.  We'll
            /// here about it first when the FAR engine discovers the symbol, and then for every
            /// reference it finds to the symbol.  However, we only want to create and pass along
            /// a single instance of <see cref="INavigableItem" /> for that definition no matter
            /// how many times we see it.
            /// 
            /// This dictionary allows us to make that mapping once and then keep it around for
            /// all future callbacks.
            /// </summary>
            private readonly Dictionary<SymbolGroup, DefinitionItem> _definitionToItem = new();

            private readonly SemaphoreSlim _gate = new(initialCount: 1);

            public IStreamingProgressTracker ProgressTracker
                => _context.ProgressTracker;

            public FindReferencesProgressAdapter(
                Solution solution, IFindUsagesContext context, FindReferencesSearchOptions options)
            {
                _solution = solution;
                _context = context;
                _options = options;
            }

            // Do nothing functions.  The streaming far service doesn't care about
            // any of these.
            public ValueTask OnStartedAsync(CancellationToken cancellationToken) => default;
            public ValueTask OnCompletedAsync(CancellationToken cancellationToken) => default;
            public ValueTask OnFindInDocumentStartedAsync(Document document, CancellationToken cancellationToken) => default;
            public ValueTask OnFindInDocumentCompletedAsync(Document document, CancellationToken cancellationToken) => default;

            // More complicated forwarding functions.  These need to map from the symbols
            // used by the FAR engine to the INavigableItems used by the streaming FAR 
            // feature.

            private async ValueTask<DefinitionItem> GetDefinitionItemAsync(SymbolGroup group, CancellationToken cancellationToken)
            {
                using (await _gate.DisposableWaitAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!_definitionToItem.TryGetValue(group, out var definitionItem))
                    {
                        definitionItem = await group.ToClassifiedDefinitionItemAsync(
                            _solution,
                            isPrimary: _definitionToItem.Count == 0,
                            includeHiddenLocations: false,
                            _options,
                            cancellationToken).ConfigureAwait(false);

                        _definitionToItem[group] = definitionItem;
                    }

                    return definitionItem;
                }
            }

            public async ValueTask OnDefinitionFoundAsync(SymbolGroup group, CancellationToken cancellationToken)
            {
                var definitionItem = await GetDefinitionItemAsync(group, cancellationToken).ConfigureAwait(false);
                await _context.OnDefinitionFoundAsync(definitionItem, cancellationToken).ConfigureAwait(false);
            }

            public async ValueTask OnReferenceFoundAsync(SymbolGroup group, ISymbol definition, ReferenceLocation location, CancellationToken cancellationToken)
            {
                var definitionItem = await GetDefinitionItemAsync(group, cancellationToken).ConfigureAwait(false);
                var referenceItem = await location.TryCreateSourceReferenceItemAsync(
                    definitionItem, includeHiddenLocations: false,
                    cancellationToken).ConfigureAwait(false);

                if (referenceItem != null)
                    await _context.OnReferenceFoundAsync(referenceItem, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
