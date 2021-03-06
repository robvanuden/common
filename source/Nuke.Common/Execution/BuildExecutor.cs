// Copyright 2019 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Nuke.Common.IO;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;

namespace Nuke.Common.Execution
{
    /// <summary>
    /// Gradually executes targets of the execution plan.
    /// Targets are skipped according to static conditions, dependency behavior, dynamic conditions and previous build attempts.
    /// </summary>
    internal static class BuildExecutor
    {
        private static PathConstruction.AbsolutePath BuildAttemptFile => Constants.GetBuildAttemptFile(NukeBuild.RootDirectory);

        public static void Execute(NukeBuild build, [CanBeNull] IReadOnlyCollection<string> skippedTargets)
        {
            var invocationHash = GetInvocationHash();
            var previouslyExecutedTargets = GetPreviouslyExecutedTargets(invocationHash);
            File.WriteAllLines(BuildAttemptFile, new[] { invocationHash });
            
            MarkSkippedTargets(build, skippedTargets);

            BuildManager.CancellationHandler += ExecuteAssuredTargets;

            try
            {
                build.ExecutionPlan.ForEach(x => Execute(build, x, previouslyExecutedTargets));
            }
            catch
            {
                ExecuteAssuredTargets();
                throw;
            }

            void ExecuteAssuredTargets()
            {
                var assuredTargets = build.ExecutionPlan.Where(x => x.AssuredAfterFailure && x.Status == ExecutionStatus.NotRun);
                assuredTargets.ForEach(x => Execute(build, x, previouslyExecutedTargets, failureMode: true));
            }
        }

        private static void Execute(
            NukeBuild build,
            ExecutableTarget target,
            IReadOnlyCollection<string> previouslyExecutedTargets,
            bool failureMode = false)
        {
            if (target.Status == ExecutionStatus.Skipped ||
                previouslyExecutedTargets.Contains(target.Name) ||
                HasSkippingCondition(target, target.DynamicConditions))
            {
                target.Status = ExecutionStatus.Skipped;
                build.OnTargetSkipped(target.Name);
                AppendToBuildAttemptFile(target.Name);
                return;
            }

            if (target.Actions.Count == 0)
            {
                target.Status = ExecutionStatus.Collective;
                return;
            }

            using (Logger.Block(target.Name))
            {
                target.Status = ExecutionStatus.Executing;
                build.OnTargetStart(target.Name);
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    target.Actions.ForEach(x => x());
                    target.Status = ExecutionStatus.Executed;
                    build.OnTargetExecuted(target.Name);
                    AppendToBuildAttemptFile(target.Name);
                }
                catch
                {
                    target.Status = ExecutionStatus.Failed;
                    build.OnTargetFailed(target.Name);
                    if (!target.ProceedAfterFailure && !failureMode)
                        throw;
                }
                finally
                {
                    target.Duration = stopwatch.Elapsed;
                }
            }
        }

        private static void MarkSkippedTargets(NukeBuild build, IReadOnlyCollection<string> skippedTargets)
        {
            void MarkTargetSkipped(ExecutableTarget target)
            {
                if (build.InvokedTargets.Contains(target))
                    return;
                
                target.Status = ExecutionStatus.Skipped;

                if (target.DependencyBehavior == DependencyBehavior.Execute)
                    return;
            
                target.ExecutionDependencies.ForEach(TryMarkTargetSkipped);
                target.Triggers.ForEach(TryMarkTargetSkipped);
            }

            void TryMarkTargetSkipped(ExecutableTarget target)
            {
                var executingTargets = build.ExecutionPlan.Where(x => x.Status == ExecutionStatus.NotRun);
                if (executingTargets.Any(x => x.ExecutionDependencies.Contains(target) || x.Triggers.Contains(target)))
                    return;

                MarkTargetSkipped(target);
            }

            if (skippedTargets != null)
            {
                build.ExecutionPlan
                    .Where(x => skippedTargets.Count == 0 || skippedTargets.Contains(x.Name, StringComparer.OrdinalIgnoreCase))
                    .ForEachLazy(x => x.SkipReason = "via --skip parameter")
                    .ForEach(MarkTargetSkipped);
            }

            build.ExecutionPlan
                .Where(x => HasSkippingCondition(x, x.StaticConditions))
                .ForEach(MarkTargetSkipped);
        }

        private static bool HasSkippingCondition(ExecutableTarget target, IEnumerable<Expression<Func<bool>>> conditions)
        {
            target.SkipReason = null; // solely for testing
            
            foreach (var condition in conditions)
            {
                if (!condition.Compile().Invoke())
                    target.SkipReason = condition.Body.ToString();
            }

            return target.SkipReason != null;
        }

        private static string GetInvocationHash()
        {
            var continueParameterName = ParameterService.Instance.GetParameterName(() => NukeBuild.Continue);
            var invocation = EnvironmentInfo.CommandLineArguments
                .Where(x => !x.StartsWith("-") || x.TrimStart("-").EqualsOrdinalIgnoreCase(continueParameterName))
                .JoinSpace();
            return invocation.GetMD5Hash();
        }
        
        private static IReadOnlyCollection<string> GetPreviouslyExecutedTargets(string invocationHash)
        {
            if (!NukeBuild.Continue ||
                !File.Exists(BuildAttemptFile))
                return new string[0];
                
            var previousBuild = File.ReadAllLines(BuildAttemptFile);
            if (previousBuild.FirstOrDefault() != invocationHash)
            {
                Logger.Warn("Build invocation changed. Starting over...");
                return new string[0];
            }

            return previousBuild.Skip(1).ToArray();
        }

        private static void AppendToBuildAttemptFile(string value)
        {
            File.AppendAllLines(BuildAttemptFile, new[] { value });
        }
    }
}
