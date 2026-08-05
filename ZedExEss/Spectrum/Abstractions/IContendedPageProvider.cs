namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Reports whether a mapped 16 KB page is subject to ULA memory contention.
    /// </summary>
    public interface IContendedPageProvider
    {
        bool IsContendedPage(int pageIndex);
    }
}
