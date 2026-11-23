namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Reports whether a mapped 16 KB page is subject to ULA memory contention.
    /// </summary>
    public interface IContendedPageProvider
    {
        bool IsContendedPage(int pageIndex);

        /// <summary>
        /// Whether the address on the bus is contended during a cycle with no memory access.
        /// The Spectrum decides by 16 KiB page; the ZX Spectrum Next by 8 KiB MMU slot.
        /// </summary>
        bool IsContendedAddress(ushort address) => IsContendedPage(address >> 14);
    }
}
