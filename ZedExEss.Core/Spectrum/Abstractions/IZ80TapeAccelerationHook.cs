namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Observes loader-relevant operands and EAR reads without coupling the CPU core to a tape implementation.
    /// </summary>
    public interface IZ80TapeAccelerationHook
    {
        void NotifyAndOperand(byte operandValue);
        void BeforeInAImmediate(ushort opcodePc, byte portLow);

        /// <summary>
        /// Observes an ED-prefixed IN r,(C) family read before the port IO cycle.
        /// These can never match IN A,(nn) loader signatures, but custom
        /// loaders poll the ULA through them and still benefit from time skipping.
        /// </summary>
        void BeforeInRegC(ushort opcodePc, byte portLow);
    }
}
