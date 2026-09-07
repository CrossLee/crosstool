namespace Crosio.Windows.Translation;

internal static class GreedyTokenSelector
{
    public static int Select(
        ReadOnlySpan<float> logits,
        int outputTokenCount,
        MarianGenerationManifest generation)
    {
        if (logits.IsEmpty)
        {
            throw new TranslationModelInvalidException("Marian 解码器返回了空 logits");
        }
        if (outputTokenCount == 0 && generation.ForcedBeginningOfSentenceTokenId is { } forced)
        {
            if ((uint)forced >= (uint)logits.Length)
            {
                throw new TranslationModelInvalidException("强制起始词元超出 Marian 词表范围");
            }
            return forced;
        }

        var suppressed = generation.SuppressedTokenIds.ToHashSet();
        suppressed.Add(generation.PaddingTokenId);
        suppressed.Add(generation.DecoderStartTokenId);
        if (outputTokenCount < generation.MinimumOutputTokens)
        {
            suppressed.Add(generation.EndOfSentenceTokenId);
        }

        var selected = -1;
        var best = float.NegativeInfinity;
        for (var tokenId = 0; tokenId < logits.Length; tokenId++)
        {
            var score = logits[tokenId];
            if (suppressed.Contains(tokenId) || float.IsNaN(score))
            {
                continue;
            }
            if (selected < 0 || score > best)
            {
                selected = tokenId;
                best = score;
            }
        }
        if (selected < 0)
        {
            throw new TranslationModelInvalidException("Marian 解码器没有可用的输出词元");
        }
        return selected;
    }
}
