// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.Classification.Classifiers;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.Classification
{
    internal partial class AbstractSyntaxClassificationService
    {
        private readonly ref struct Worker
        {
            private readonly SemanticModel _semanticModel;
            private readonly SyntaxTree _syntaxTree;
            private readonly TextSpanIntervalTree _textSpanIntervalTree;
            private readonly SegmentedList<ClassifiedSpan> _list;
            private readonly CancellationToken _cancellationToken;
            private readonly Func<SyntaxNode, ImmutableArray<ISyntaxClassifier>> _getNodeClassifiers;
            private readonly Func<SyntaxToken, ImmutableArray<ISyntaxClassifier>> _getTokenClassifiers;
            private readonly SegmentedHashSet<ClassifiedSpan> _set;
            private readonly Stack<SyntaxNodeOrToken> _pendingNodes;
            private readonly ClassificationOptions _options;

            private static readonly ObjectPool<SegmentedList<ClassifiedSpan>> s_listPool = new(() => new());

            private Worker(
                SemanticModel semanticModel,
                TextSpan[] textSpans,
                SegmentedList<ClassifiedSpan> list,
                Func<SyntaxNode, ImmutableArray<ISyntaxClassifier>> getNodeClassifiers,
                Func<SyntaxToken, ImmutableArray<ISyntaxClassifier>> getTokenClassifiers,
                ClassificationOptions options,
                CancellationToken cancellationToken)
            {
                _getNodeClassifiers = getNodeClassifiers;
                _getTokenClassifiers = getTokenClassifiers;
                _semanticModel = semanticModel;
                _syntaxTree = semanticModel.SyntaxTree;
                _textSpanIntervalTree = new TextSpanIntervalTree(textSpans);
                _list = list;
                _cancellationToken = cancellationToken;
                _options = options;

                // get one from pool
                _set = SharedPools.Default<SegmentedHashSet<ClassifiedSpan>>().AllocateAndClear();
                _pendingNodes = SharedPools.Default<Stack<SyntaxNodeOrToken>>().AllocateAndClear();
            }

            internal static void Classify(
                SemanticModel semanticModel,
                TextSpan[] textSpans,
                SegmentedList<ClassifiedSpan> list,
                Func<SyntaxNode, ImmutableArray<ISyntaxClassifier>> getNodeClassifiers,
                Func<SyntaxToken, ImmutableArray<ISyntaxClassifier>> getTokenClassifiers,
                ClassificationOptions options,
                CancellationToken cancellationToken)
            {
                using var worker = new Worker(semanticModel, textSpans, list, getNodeClassifiers, getTokenClassifiers, options, cancellationToken);

                worker._pendingNodes.Push(worker._syntaxTree.GetRoot(cancellationToken));
                worker.ProcessNodes();
            }

            public void Dispose()
            {
                // Deliberately do not call ClearAndFree for the set as we can easily have a set that goes past the
                // threshold simply with a single classified screen.  This allows reuse of those sets without causing
                // lots of garbage.
                _set.Clear();
                SharedPools.Default<SegmentedHashSet<ClassifiedSpan>>().Free(_set);
                SharedPools.Default<Stack<SyntaxNodeOrToken>>().ClearAndFree(this._pendingNodes);
            }

            private void AddClassification(TextSpan textSpan, string type)
            {
                if (textSpan.Length > 0 && _textSpanIntervalTree.HasIntervalThatIntersectsWith(textSpan))
                {
                    var tuple = new ClassifiedSpan(type, textSpan);
                    if (!_set.Contains(tuple))
                    {
                        _list.Add(tuple);
                        _set.Add(tuple);
                    }
                }
            }

            private void ProcessNodes()
            {
                while (_pendingNodes.Count > 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var nodeOrToken = _pendingNodes.Pop();
                    var intervalsThatIntersect = _textSpanIntervalTree.GetIntervalsThatIntersectWith(
                            nodeOrToken.FullSpan.Start, nodeOrToken.FullSpan.Length);

                    if (intervalsThatIntersect.Length > 0)
                    {
                        foreach (var interval in intervalsThatIntersect)
                        {
                            ClassifyNodeOrToken(nodeOrToken, interval);
                        }

                        foreach (var child in nodeOrToken.ChildNodesAndTokens())
                        {
                            _pendingNodes.Push(child);
                        }
                    }
                }
            }

            private void ClassifyNodeOrToken(SyntaxNodeOrToken nodeOrToken, TextSpan textSpan)
            {
                var node = nodeOrToken.AsNode();
                if (node != null)
                {
                    ClassifyNode(node, textSpan);
                }
                else
                {
                    ClassifyToken(nodeOrToken.AsToken(), textSpan);
                }
            }

            private void ClassifyNode(SyntaxNode syntax, TextSpan textSpan)
            {
                using var obj = s_listPool.GetPooledObject();
                var list = obj.Object;

                foreach (var classifier in _getNodeClassifiers(syntax))
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    list.Clear();
                    classifier.AddClassifications(syntax, textSpan, _semanticModel, _options, list, _cancellationToken);
                    AddClassifications(list);
                }
            }

            private void AddClassifications(SegmentedList<ClassifiedSpan> classifications)
            {
                foreach (var classification in classifications)
                    AddClassification(classification);
            }

            private void AddClassification(ClassifiedSpan classification)
            {
                if (classification.ClassificationType != null)
                {
                    AddClassification(classification.TextSpan, classification.ClassificationType);
                }
            }

            private void ClassifyToken(SyntaxToken syntax, TextSpan textSpan)
            {
                ClassifyStructuredTrivia(syntax.LeadingTrivia);

                using var obj = s_listPool.GetPooledObject();
                var list = obj.Object;

                foreach (var classifier in _getTokenClassifiers(syntax))
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    list.Clear();
                    classifier.AddClassifications(syntax, textSpan, _semanticModel, _options, list, _cancellationToken);
                    AddClassifications(list);
                }

                ClassifyStructuredTrivia(syntax.TrailingTrivia);
            }

            private void ClassifyStructuredTrivia(SyntaxTriviaList triviaList)
            {
                foreach (var trivia in triviaList)
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    if (trivia.HasStructure)
                    {
                        _pendingNodes.Push(trivia.GetStructure());
                    }
                }
            }
        }
    }
}
