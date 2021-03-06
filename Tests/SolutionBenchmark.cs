﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Clustering;
using Clustering.Benchmarking;
using Clustering.Benchmarking.Results;
using Clustering.SolutionModel.Nodes;
using Clustering.SolutionModel.Serializing;
using Tests.Building.TestExtensions;

namespace Tests
{
    public static class SolutionBenchmark
    {
        private static string ParsedRepoLocation(Repository repo) =>
            $@"{LocalPathConfig.ParsedDataLocation}\{repo.Owner}\{repo.Name}\";

        private static string RepositoryLocation(Repository repo) =>
            $@"{LocalPathConfig.RepoLocations}\{repo.Name}\{repo.Solution}";

        public static PerSolutionResultsContainer BenchNamespaceRecovery(
            IReadOnlyCollection<IBenchmarkConfig> benchMarkConfigs,
            IList<Repository> repos, int rerunsPerConfig)
        {
            var repoScores = new Dictionary<Repository, Dictionary<IBenchmarkConfig, BenchMarkResult>>();

            foreach (var repository in repos.ToList())
            {
                var dataFolder = ParsedRepoLocation(repository);
                var projectGraphsInFolder = BenchMark.GetProjectGraphsInFolder(dataFolder).ToList();

                var perProjectConfigEntry = new Dictionary<IBenchmarkConfig, List<BenchMarkResultsEntry>>();
                foreach (var project in projectGraphsInFolder)
                {
                    var leafNamespaces = BenchMark.RootNamespaces(project);

                    foreach (var config in benchMarkConfigs)
                    {
                        var results =
                            Enumerable.Range(0, rerunsPerConfig)
                                .Select(x => BenchMark.Run(config, leafNamespaces))
                                .ToList();
                        var benchMarkResult = new BenchMarkResultsEntry(project.Name, results.Average());

                        if (!perProjectConfigEntry.ContainsKey(config))
                            perProjectConfigEntry[config] = new List<BenchMarkResultsEntry>();
                        perProjectConfigEntry[config].Add(benchMarkResult);
                    }
                }

                var flattenedConfigEntries = perProjectConfigEntry.ToDictionary(x => x.Key, x => x.Value.Average());

                repoScores.Add(repository, flattenedConfigEntries);
            }

            return new PerSolutionResultsContainer(repoScores, rerunsPerConfig);
        }

        public static PerSolutionResultsContainer BenchMarkProjectRecovery(
            IReadOnlyCollection<IBenchmarkConfig> benchMarkConfigs,
            IList<Repository> repos, int rerunsPerConfig)
        {
            var repoScores = new Dictionary<Repository, Dictionary<IBenchmarkConfig, BenchMarkResult>>();

            foreach (var repository in repos.ToList())
            {
                var dataFolder = ParsedRepoLocation(repository);
                var graph = BenchMark.GetCompleteTreeWithDependencies(dataFolder);

                var leafNamespaces = BenchMark.RootNamespaces(graph);

                leafNamespaces = new NonNestedClusterGraph(leafNamespaces.Clusters, leafNamespaces.Edges);

                var configEntries =
                    (from config in benchMarkConfigs
                        let tasks =
                            Enumerable.Range(0, rerunsPerConfig)
                                .Select(x => Task.Run(() => BenchMark.Run(config.Clone(), leafNamespaces)))
                        let average = Task.WhenAll(tasks).Result.Average()
                        select new {config, average})
                        .ToDictionary(averagePerConfig => averagePerConfig.config,
                            averagePerConfig => averagePerConfig.average);

                repoScores.Add(repository, configEntries);
            }

            return new PerSolutionResultsContainer(repoScores, rerunsPerConfig);
        }

        public static PerSolutionResultsContainer BenchMarkProjectRecoveryWithRemovedData(IBenchmarkConfig config,
            IList<Repository> repos, int rerunsPerConfig, double dependencyMultiplier)
        {
            var repoScores = new Dictionary<Repository, Dictionary<IBenchmarkConfig, BenchMarkResult>>();
            config.Name = config.Name + $"-{dependencyMultiplier*100}%";
            foreach (var repository in repos.ToList())
            {
                var dataFolder = ParsedRepoLocation(repository);
                var graph = BenchMark.GetCompleteTreeWithDependencies(dataFolder);

                var leafNamespaces = BenchMark.RootNamespaces(graph);

                // REMOVE DEPENDENCIES
                var newEdges = (from edge in leafNamespaces.Edges
                    let newDeps = new HashSet<Node>(edge.Take((int) (edge.Count()* dependencyMultiplier)))
                    select new {edge.Key, newDeps})
                    .ToDictionary(x => x.Key, x => x.newDeps)
                    .SelectMany(p => p.Value
                        .Select(x => new {p.Key, Value = x}))
                    .ToLookup(pair => pair.Key, pair => pair.Value);

                leafNamespaces = new NonNestedClusterGraph(leafNamespaces.Clusters, newEdges);

                var tasks =
                    Enumerable.Range(0, rerunsPerConfig)
                        .Select(x => Task.Run(() => BenchMark.Run(config.Clone(), leafNamespaces)));
                var average = Task.WhenAll(tasks).Result.Average();

                var dict = new Dictionary<IBenchmarkConfig, BenchMarkResult> {{config, average}};
                repoScores.Add(repository, dict);
            }

            return new PerSolutionResultsContainer(repoScores, rerunsPerConfig);
        }

        public static PerSolutionResultsContainer CompareProjectRecovery(
            IBenchmarkConfig config1,IBenchmarkConfig config2,
            IList<Repository> repos, int rerunsPerConfig)
        {
            var repoScores = new Dictionary<Repository, Dictionary<IBenchmarkConfig, BenchMarkResult>>();

            foreach (var repository in repos.ToList())
            {
                var dataFolder = ParsedRepoLocation(repository);
                var graph = BenchMark.GetCompleteTreeWithDependencies(dataFolder);

                var leafNamespaces = BenchMark.RootNamespaces(graph);

                leafNamespaces = new NonNestedClusterGraph(leafNamespaces.Clusters, leafNamespaces.Edges);

                var tasks =
                    Enumerable.Range(0, rerunsPerConfig)
                        .Select(x => Task.Run(() => BenchMark.CompareResultsOf2Algs(
                            config1.Clone().ClusteringAlgorithm,
                            config2.Clone().ClusteringAlgorithm,
                            config1.Clone().CuttingAlgorithm,
                            config1.Clone().SimilarityMectric
                            , leafNamespaces)));

                var average = Task.WhenAll(tasks).Result.Average();
                config1.Name = "Dep->Usage-Equallity";
                var configEntries = new Dictionary<IBenchmarkConfig, BenchMarkResult> { {config1, average}};
                repoScores.Add(repository, configEntries);
            }

            return new PerSolutionResultsContainer(repoScores, rerunsPerConfig);
        }

        public static void Prepare(Repository repo)
        {
            BenchMark.Prepare(RepositoryLocation(repo), ParsedRepoLocation(repo));
        }
    }
}
