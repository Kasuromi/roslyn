﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Shared.Extensions
{
    internal static partial class SyntaxGeneratorExtensions
    {
        private static readonly ImmutableDictionary<BinaryOperatorKind, BinaryOperatorKind> s_negatedBinaryMap =
            new Dictionary<BinaryOperatorKind, BinaryOperatorKind>
            {
                { BinaryOperatorKind.Equals, BinaryOperatorKind.NotEquals },
                { BinaryOperatorKind.NotEquals, BinaryOperatorKind.Equals },
                { BinaryOperatorKind.LessThan, BinaryOperatorKind.GreaterThanOrEqual },
                { BinaryOperatorKind.GreaterThan, BinaryOperatorKind.LessThanOrEqual },
                { BinaryOperatorKind.LessThanOrEqual, BinaryOperatorKind.GreaterThan },
                { BinaryOperatorKind.GreaterThanOrEqual, BinaryOperatorKind.LessThan },
                { BinaryOperatorKind.Or, BinaryOperatorKind.And },
                { BinaryOperatorKind.And, BinaryOperatorKind.Or },
                { BinaryOperatorKind.ConditionalOr, BinaryOperatorKind.ConditionalAnd },
                { BinaryOperatorKind.ConditionalAnd, BinaryOperatorKind.ConditionalOr },
            }.ToImmutableDictionary();

        public static SyntaxNode Negate(
            this SyntaxGenerator generator,
            SyntaxGeneratorInternal generatorInternal,
            SyntaxNode expression,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            return Negate(generator, generatorInternal, expression, semanticModel, negateBinary: true, cancellationToken);
        }

        public static SyntaxNode Negate(
            this SyntaxGenerator generator,
            SyntaxGeneratorInternal generatorInternal,
            SyntaxNode expressionOrPattern,
            SemanticModel semanticModel,
            bool negateBinary,
            CancellationToken cancellationToken)
        {
            var syntaxFacts = generatorInternal.SyntaxFacts;
            if (syntaxFacts.IsParenthesizedExpression(expressionOrPattern))
            {
                return generatorInternal.AddParentheses(
                    generator.Negate(
                        generatorInternal,
                        syntaxFacts.GetExpressionOfParenthesizedExpression(expressionOrPattern),
                        semanticModel,
                        negateBinary,
                        cancellationToken))
                    .WithTriviaFrom(expressionOrPattern);
            }

            if (negateBinary && syntaxFacts.IsBinaryExpression(expressionOrPattern))
                return GetNegationOfBinaryExpression(expressionOrPattern, generator, generatorInternal, semanticModel, cancellationToken);

            if (syntaxFacts.IsLiteralExpression(expressionOrPattern))
                return GetNegationOfLiteralExpression(expressionOrPattern, generator, semanticModel);

            if (syntaxFacts.IsLogicalNotExpression(expressionOrPattern))
                return GetNegationOfLogicalNotExpression(expressionOrPattern, syntaxFacts);

            if (negateBinary && syntaxFacts.IsIsPatternExpression(expressionOrPattern))
                return GetNegationOfIsPatternExpression(expressionOrPattern, generator, generatorInternal, semanticModel, cancellationToken);

            if (syntaxFacts.IsParenthesizedPattern(expressionOrPattern))
            {
                // Push the negation inside the parenthesized pattern.
                return generatorInternal.AddParentheses(
                    generator.Negate(
                        generatorInternal,
                        syntaxFacts.GetPatternOfParenthesizedPattern(expressionOrPattern),
                        semanticModel,
                        negateBinary,
                        cancellationToken))
                    .WithTriviaFrom(expressionOrPattern);
            }

            if (negateBinary && syntaxFacts.IsBinaryPattern(expressionOrPattern))
                return GetNegationOfBinaryPattern(expressionOrPattern, generator, generatorInternal, semanticModel, cancellationToken);

            if (syntaxFacts.IsConstantPattern(expressionOrPattern))
                return GetNegationOfConstantPattern(expressionOrPattern, generator, generatorInternal);

            if (syntaxFacts.IsUnaryPattern(expressionOrPattern))
                return GetNegationOfUnaryPattern(expressionOrPattern, generatorInternal, syntaxFacts);

            // TODO(cyrusn): We could support negating relational patterns in the future.  i.e.
            //
            //      not >= 0   ->    < 0

            return syntaxFacts.IsAnyPattern(expressionOrPattern)
                ? generatorInternal.NotPattern(expressionOrPattern)
                : generator.LogicalNotExpression(expressionOrPattern);
        }

        private static SyntaxNode GetNegationOfBinaryExpression(
            SyntaxNode expressionNode,
            SyntaxGenerator generator,
            SyntaxGeneratorInternal generatorInternal,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            var syntaxFacts = generatorInternal.SyntaxFacts;
            syntaxFacts.GetPartsOfBinaryExpression(expressionNode, out var leftOperand, out var operatorToken, out var rightOperand);

            var operation = semanticModel.GetOperation(expressionNode, cancellationToken);
            if (operation is not IBinaryOperation binaryOperation)
            {
                // x is y   ->    x is not y
                if (syntaxFacts.IsIsExpression(expressionNode) && syntaxFacts.SupportsNotPattern(semanticModel.SyntaxTree.Options))
                    return generatorInternal.IsPatternExpression(leftOperand, operatorToken, generatorInternal.NotPattern(generatorInternal.TypePattern(rightOperand)));

                // Apply the logical not operator if it is not a binary operation.
                return generator.LogicalNotExpression(expressionNode);
            }

            if (!s_negatedBinaryMap.TryGetValue(binaryOperation.OperatorKind, out var negatedKind))
                return generator.LogicalNotExpression(expressionNode);

            if (binaryOperation.OperatorKind is BinaryOperatorKind.Or or
                                                BinaryOperatorKind.And or
                                                BinaryOperatorKind.ConditionalAnd or
                                                BinaryOperatorKind.ConditionalOr)
            {
                leftOperand = generator.Negate(generatorInternal, leftOperand, semanticModel, cancellationToken);
                rightOperand = generator.Negate(generatorInternal, rightOperand, semanticModel, cancellationToken);
            }

            var newBinaryExpressionSyntax = negatedKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
                ? generatorInternal.NegateEquality(generator, expressionNode, leftOperand, negatedKind, rightOperand)
                : NegateRelational(generator, binaryOperation, leftOperand, negatedKind, rightOperand);
            newBinaryExpressionSyntax = newBinaryExpressionSyntax.WithTriviaFrom(expressionNode);

            var newToken = syntaxFacts.GetOperatorTokenOfBinaryExpression(newBinaryExpressionSyntax);
            return newBinaryExpressionSyntax.ReplaceToken(
                newToken,
                newToken.WithTriviaFrom(operatorToken));
        }

        private static SyntaxNode GetNegationOfBinaryPattern(
            SyntaxNode pattern,
            SyntaxGenerator generator,
            SyntaxGeneratorInternal generatorInternal,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            // Apply demorgans's law here.
            //
            //  not (a and b)   ->   not a or not b
            //  not (a or b)    ->   not a and not b

            var syntaxFacts = generatorInternal.SyntaxFacts;
            syntaxFacts.GetPartsOfBinaryPattern(pattern, out var left, out var operatorToken, out var right);

            var newLeft = generator.Negate(generatorInternal, left, semanticModel, cancellationToken);
            var newRight = generator.Negate(generatorInternal, right, semanticModel, cancellationToken);

            var newPattern =
                syntaxFacts.IsAndPattern(pattern) ? generatorInternal.OrPattern(newLeft, newRight) :
                syntaxFacts.IsOrPattern(pattern) ? generatorInternal.AndPattern(newLeft, newRight) :
                throw ExceptionUtilities.UnexpectedValue(pattern.RawKind);

            newPattern = newPattern.WithTriviaFrom(pattern);

            syntaxFacts.GetPartsOfBinaryPattern(newPattern, out _, out var newToken, out _);
            var newTokenWithTrivia = newToken.WithTriviaFrom(operatorToken);
            return newPattern.ReplaceToken(newToken, newTokenWithTrivia);
        }

        private static SyntaxNode GetNegationOfIsPatternExpression(SyntaxNode isExpression, SyntaxGenerator generator, SyntaxGeneratorInternal generatorInternal, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            // Don't recurse into patterns if the language doesn't support negated patterns.
            // Just wrap with a normal '!' expression.
            var syntaxFacts = generatorInternal.SyntaxFacts;
            if (syntaxFacts.SupportsNotPattern(semanticModel.SyntaxTree.Options))
            {
                syntaxFacts.GetPartsOfIsPatternExpression(isExpression, out var left, out var isToken, out var pattern);
                var negated = generator.Negate(generatorInternal, pattern, semanticModel, cancellationToken);

                // Negating the pattern may have formed something illegal.  If so, just do a normal `!` negation.
                if (IsLegalPattern(syntaxFacts, negated, designatorsLegal: true))
                {
                    if (syntaxFacts.IsTypePattern(negated))
                    {
                        // We started with `x is not t`.  Unwrap the type pattern for 't' and create a simple `is` binary expr `x is t`.
                        var type = syntaxFacts.GetTypeOfTypePattern(negated);
                        return generator.IsTypeExpression(left, type);
                    }
                    else
                    {
                        // Keep this as a normal `is-pattern`, just with the pattern portion negated.
                        return generatorInternal.IsPatternExpression(left, isToken, negated);
                    }
                }
            }

            return generator.LogicalNotExpression(isExpression);
        }

        private static bool IsLegalPattern(ISyntaxFacts syntaxFacts, SyntaxNode pattern, bool designatorsLegal)
        {
            // It is illegal to create a pattern that has a designator under a not-pattern or or-pattern
            if (syntaxFacts.IsBinaryPattern(pattern))
            {
                syntaxFacts.GetPartsOfBinaryPattern(pattern, out var left, out _, out var right);
                designatorsLegal = designatorsLegal && !syntaxFacts.IsOrPattern(pattern);
                return IsLegalPattern(syntaxFacts, left, designatorsLegal) &&
                       IsLegalPattern(syntaxFacts, right, designatorsLegal);
            }

            if (syntaxFacts.IsNotPattern(pattern))
            {
                syntaxFacts.GetPartsOfUnaryPattern(pattern, out _, out var subPattern);
                return IsLegalPattern(syntaxFacts, subPattern, designatorsLegal: false);
            }

            if (syntaxFacts.IsParenthesizedPattern(pattern))
            {
                syntaxFacts.GetPartsOfParenthesizedPattern(pattern, out _, out var subPattern, out _);
                return IsLegalPattern(syntaxFacts, subPattern, designatorsLegal);
            }

            if (syntaxFacts.IsDeclarationPattern(pattern))
            {
                syntaxFacts.GetPartsOfDeclarationPattern(pattern, out _, out var designator);
                return designator == null || designatorsLegal;
            }

            if (syntaxFacts.IsRecursivePattern(pattern))
            {
                syntaxFacts.GetPartsOfRecursivePattern(pattern, out _, out _, out _, out var designator);
                return designator == null || designatorsLegal;
            }

            if (syntaxFacts.IsVarPattern(pattern))
                return designatorsLegal;

            return true;
        }

        private static SyntaxNode NegateRelational(
            SyntaxGenerator generator,
            IBinaryOperation binaryOperation,
            SyntaxNode leftOperand,
            BinaryOperatorKind operationKind,
            SyntaxNode rightOperand)
        {
            return operationKind switch
            {
                BinaryOperatorKind.LessThanOrEqual => IsSpecialCaseBinaryExpression(binaryOperation, operationKind)
                    ? generator.ValueEqualsExpression(leftOperand, rightOperand)
                    : generator.LessThanOrEqualExpression(leftOperand, rightOperand),
                BinaryOperatorKind.GreaterThanOrEqual => IsSpecialCaseBinaryExpression(binaryOperation, operationKind)
                    ? generator.ValueEqualsExpression(leftOperand, rightOperand)
                    : generator.GreaterThanOrEqualExpression(leftOperand, rightOperand),
                BinaryOperatorKind.LessThan => generator.LessThanExpression(leftOperand, rightOperand),
                BinaryOperatorKind.GreaterThan => generator.GreaterThanExpression(leftOperand, rightOperand),
                BinaryOperatorKind.Or => generator.BitwiseOrExpression(leftOperand, rightOperand),
                BinaryOperatorKind.And => generator.BitwiseAndExpression(leftOperand, rightOperand),
                BinaryOperatorKind.ConditionalOr => generator.LogicalOrExpression(leftOperand, rightOperand),
                BinaryOperatorKind.ConditionalAnd => generator.LogicalAndExpression(leftOperand, rightOperand),
                _ => throw ExceptionUtilities.UnexpectedValue(operationKind),
            };
        }

        /// <summary>
        /// Returns true if the binaryExpression consists of an expression that can never be negative, 
        /// such as length or unsigned numeric types, being compared to zero with greater than, 
        /// less than, or equals relational operator.
        /// </summary>
        public static bool IsSpecialCaseBinaryExpression(
            IBinaryOperation binaryOperation,
            BinaryOperatorKind operationKind)
        {
            if (binaryOperation == null)
                return false;

            var rightOperand = RemoveImplicitConversion(binaryOperation.RightOperand);
            var leftOperand = RemoveImplicitConversion(binaryOperation.LeftOperand);

            return operationKind switch
            {
                BinaryOperatorKind.LessThanOrEqual when rightOperand.IsNumericLiteral()
                    => CanSimplifyToLengthEqualsZeroExpression(leftOperand, (ILiteralOperation)rightOperand),
                BinaryOperatorKind.GreaterThanOrEqual when leftOperand.IsNumericLiteral()
                    => CanSimplifyToLengthEqualsZeroExpression(rightOperand, (ILiteralOperation)leftOperand),
                _ => false,
            };
        }

        private static IOperation RemoveImplicitConversion(IOperation operation)
        {
            return operation is IConversionOperation conversion && conversion.IsImplicit
                ? RemoveImplicitConversion(conversion.Operand)
                : operation;
        }

        private static bool CanSimplifyToLengthEqualsZeroExpression(
            IOperation variableExpression, ILiteralOperation numericLiteralExpression)
        {
            var numericValue = numericLiteralExpression.ConstantValue;
            if (numericValue.HasValue && numericValue.Value is 0)
            {
                if (variableExpression is IPropertyReferenceOperation propertyOperation)
                {
                    var property = propertyOperation.Property;
                    if (property.Name is nameof(Array.Length) or nameof(Array.LongLength))
                    {
                        var containingType = property.ContainingType;
                        if (containingType?.SpecialType == SpecialType.System_Array ||
                            containingType?.SpecialType == SpecialType.System_String)
                        {
                            return true;
                        }
                    }
                }

                var type = variableExpression.Type;
                switch (type?.SpecialType)
                {
                    case SpecialType.System_Byte:
                    case SpecialType.System_UInt16:
                    case SpecialType.System_UInt32:
                    case SpecialType.System_UInt64:
                        return true;
                }
            }

            return false;
        }

        private static SyntaxNode GetNegationOfLiteralExpression(
            SyntaxNode expression,
            SyntaxGenerator generator,
            SemanticModel semanticModel)
        {
            var operation = semanticModel.GetOperation(expression);
            SyntaxNode newLiteralExpression;

            if (operation?.Kind == OperationKind.Literal && operation.ConstantValue.HasValue && operation.ConstantValue.Value is true)
            {
                newLiteralExpression = generator.FalseLiteralExpression();
            }
            else if (operation?.Kind == OperationKind.Literal && operation.ConstantValue.HasValue && operation.ConstantValue.Value is false)
            {
                newLiteralExpression = generator.TrueLiteralExpression();
            }
            else
            {
                newLiteralExpression = generator.LogicalNotExpression(expression.WithoutTrivia());
            }

            return newLiteralExpression.WithTriviaFrom(expression);
        }

        private static SyntaxNode GetNegationOfConstantPattern(
            SyntaxNode pattern,
            SyntaxGenerator generator,
            SyntaxGeneratorInternal generatorInternal)
        {
            var syntaxFacts = generatorInternal.SyntaxFacts;

            // If we have `is true/false` just swap that to be `is false/true`.

            var expression = syntaxFacts.GetExpressionOfConstantPattern(pattern);
            if (syntaxFacts.IsTrueLiteralExpression(expression))
                return generatorInternal.ConstantPattern(generator.FalseLiteralExpression());

            if (syntaxFacts.IsFalseLiteralExpression(expression))
                return generatorInternal.ConstantPattern(generator.TrueLiteralExpression());

            // Otherwise, just negate the entire pattern, we don't have anything else special we can do here.
            return generatorInternal.NotPattern(pattern);
        }

        private static SyntaxNode GetNegationOfLogicalNotExpression(
            SyntaxNode expression,
            ISyntaxFacts syntaxFacts)
        {
            var operatorToken = syntaxFacts.GetOperatorTokenOfPrefixUnaryExpression(expression);
            var operand = syntaxFacts.GetOperandOfPrefixUnaryExpression(expression);

            return operand.WithPrependedLeadingTrivia(operatorToken.LeadingTrivia)
                          .WithAdditionalAnnotations(Simplifier.Annotation);
        }

        private static SyntaxNode GetNegationOfUnaryPattern(
            SyntaxNode pattern,
            SyntaxGeneratorInternal generatorInternal,
            ISyntaxFacts syntaxFacts)
        {
            syntaxFacts.GetPartsOfUnaryPattern(pattern, out var opToken, out var subPattern);

            // not not p    ->   p
            if (syntaxFacts.IsNotPattern(pattern))
            {
                return subPattern.WithPrependedLeadingTrivia(opToken.LeadingTrivia)
                                 .WithAdditionalAnnotations(Simplifier.Annotation);
            }

            // If there are other interesting unary patterns in the future, we can support specialized logic for
            // negating them here.
            return generatorInternal.NotPattern(pattern);
        }
    }
}
