using Helldivers2ModManager.Core.Profiles;

namespace Helldivers2ModManager.Core.Deployment;

public static class DeploymentOrderBuilder
{
    public static IReadOnlyList<ModDeploymentInput> Build(
        IReadOnlyList<ModDeploymentInput> enabledInputs,
        IReadOnlyList<Guid>? preferredOrder,
        bool useDeploymentOrder,
        IReadOnlyList<Guid> deploymentOrderGuids,
        bool deployBottomToTop) =>
        Build(
            enabledInputs,
            static input => input.Guid,
            preferredOrder,
            useDeploymentOrder,
            deploymentOrderGuids,
            deployBottomToTop);

    public static IReadOnlyList<T> Build<T>(
        IReadOnlyList<T> enabledInputs,
        Func<T, Guid> guidSelector,
        IReadOnlyList<Guid>? preferredOrder,
        bool useDeploymentOrder,
        IReadOnlyList<Guid> deploymentOrderGuids,
        bool deployBottomToTop)
    {
        IEnumerable<T> ordered = enabledInputs;
        if (preferredOrder is { Count: > 0 })
        {
            var byGuid = enabledInputs.ToDictionary(guidSelector);
            var reordered = new List<T>();
            foreach (var guid in preferredOrder)
            {
                if (byGuid.Remove(guid, out var input))
                {
                    reordered.Add(input);
                }
            }

            reordered.AddRange(byGuid.Values);
            ordered = reordered;
        }

        var result = ordered.ToList();
        if (useDeploymentOrder && deploymentOrderGuids.Count > 0)
        {
            var byGuid = result.ToDictionary(guidSelector);
            var explicitOrder = new List<T>();
            foreach (var guid in deploymentOrderGuids)
            {
                if (byGuid.Remove(guid, out var input))
                {
                    explicitOrder.Add(input);
                }
            }

            explicitOrder.AddRange(byGuid.Values);
            result = explicitOrder;
        }

        if (deployBottomToTop)
        {
            result.Reverse();
        }

        return result;
    }
}
