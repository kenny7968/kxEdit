namespace kxEdit.Core.Editing;

public static class ImeAttribute
{
    public const byte Input = 0x00;
    public const byte TargetConverted = 0x01;
    public const byte Converted = 0x02;
    public const byte TargetNotConverted = 0x03;
    public const byte InputError = 0x04;
    public const byte FixedConverted = 0x05;
}
