namespace ZedExEss.ZxNext.Ports;

/// <summary>Direct NextReg write path used by the Z80N NEXTREG instructions.</summary>
public interface IZxNextRegisterBus
{
    void WriteNextRegister(byte register, byte value);
}
