using Study.Core.Models;

namespace Study.Core.Plc;

public static class SlotDetector
{
    public const int MaxSlot = 16;

    /// <summary>
    /// Finds the CPU slot by asking each slot for its tag list (read-only). Returns null if no
    /// slot answers. Slot 0 is tried first because that is where almost every CPU sits.
    /// </summary>
    public static async Task<int?> DetectAsync(ITagBrowser browser, PlcEntry plc, TimeSpan perSlotTimeout,
        IProgress<int>? progress, CancellationToken ct)
    {
        for (var slot = 0; slot <= MaxSlot; slot++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(slot);
            try
            {
                await browser.ListTagsAsync(plc with { Slot = slot }, null, perSlotTimeout, ct).ConfigureAwait(false);
                return slot;
            }
            catch (PlcException)
            {
                // Not this slot; keep going.
            }
        }

        return null;
    }
}
