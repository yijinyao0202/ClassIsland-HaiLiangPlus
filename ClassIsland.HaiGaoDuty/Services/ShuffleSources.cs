using System.Security.Cryptography;

namespace ClassIsland.HaiGaoDuty.Services;

internal interface IShuffleSource
{
    int Next(int maxExclusive);
}

internal sealed class CryptoShuffleSource : IShuffleSource
{
    public int Next(int maxExclusive) => RandomNumberGenerator.GetInt32(maxExclusive);
}
