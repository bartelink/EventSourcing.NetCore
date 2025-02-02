﻿using System.Threading;
using System.Threading.Tasks;

namespace Core.Subscriptions
{
    public interface ISubscriptionCheckpointRepository
    {
        ValueTask<ulong?> Load(string subscriptionId, CancellationToken ct);

        ValueTask Store(string subscriptionId, ulong position, CancellationToken ct);
    }
}
