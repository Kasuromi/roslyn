﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.UseParameterNullChecking
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = PredefinedCodeFixProviderNames.UseParameterNullChecking), Shared]
    internal sealed class CSharpUseParameterNullCheckingCodeFixProvider : SyntaxEditorBasedCodeFixProvider
    {
        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public CSharpUseParameterNullCheckingCodeFixProvider()
        {
        }

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(IDEDiagnosticIds.UseParameterNullCheckingId);

        internal sealed override CodeFixCategory CodeFixCategory => CodeFixCategory.CodeStyle;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics[0];
            context.RegisterCodeFix(
                new MyCodeAction(c => FixAsync(context.Document, diagnostic, c)),
                context.Diagnostics);
            return Task.CompletedTask;
        }

        protected override Task FixAllAsync(
            Document document, ImmutableArray<Diagnostic> diagnostics,
            SyntaxEditor editor, CancellationToken cancellationToken)
        {
            // Tracking parameters which have already been fixed by a fix-all operation.
            // This avoids crashing the fixer when the same parameter is null-tested multiple times.
            using var _ = PooledHashSet<Location>.GetInstance(out var fixedParameterLocations);
            foreach (var diagnostic in diagnostics)
            {
                var node = diagnostic.Location.FindNode(getInnermostNodeForTie: true, cancellationToken: cancellationToken);
                switch (node)
                {
                    case IfStatementSyntax ifStatement:
                        editor.RemoveNode(ifStatement);
                        break;
                    case ExpressionStatementSyntax expressionStatement:
                        // this.item = item ?? throw new ArgumentNullException(nameof(item));
                        var assignment = (AssignmentExpressionSyntax)expressionStatement.Expression;
                        var nullCoalescing = (BinaryExpressionSyntax)assignment.Right;
                        var parameterReferenceSyntax = nullCoalescing.Left;
                        editor.ReplaceNode(nullCoalescing, parameterReferenceSyntax.WithAppendedTrailingTrivia(SyntaxFactory.ElasticMarker));
                        break;
                    default:
                        throw ExceptionUtilities.UnexpectedValue(node);
                }

                var parameterLocation = diagnostic.AdditionalLocations[0];
                if (fixedParameterLocations.Add(parameterLocation))
                {
                    var parameterSyntax = (ParameterSyntax)parameterLocation.FindNode(cancellationToken);
                    if (parameterSyntax.ExclamationExclamationToken.IsKind(SyntaxKind.None))
                    {
                        var identifier = parameterSyntax.Identifier;
                        var newIdentifier = identifier.WithoutTrailingTrivia();
                        var newExclamationExclamationToken = SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.ExclamationExclamationToken, identifier.TrailingTrivia);
                        editor.ReplaceNode(parameterSyntax, parameterSyntax.Update(
                            parameterSyntax.AttributeLists,
                            parameterSyntax.Modifiers,
                            parameterSyntax.Type,
                            newIdentifier,
                            newExclamationExclamationToken,
                            parameterSyntax.Default));
                    }
                }
            }

            return Task.CompletedTask;
        }

        private class MyCodeAction : CustomCodeActions.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(CSharpAnalyzersResources.Use_parameter_null_checking, createChangedDocument, nameof(CSharpUseParameterNullCheckingCodeFixProvider))
            {
            }
        }
    }
}
