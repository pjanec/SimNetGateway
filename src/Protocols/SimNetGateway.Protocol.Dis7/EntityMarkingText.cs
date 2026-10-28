#nullable enable
using System;
using System.Text;
using DISnet;

namespace SimNetGateway.Protocol.Dis7;

/// <summary>
/// The entity marking (IEEE 1278.1-2012 section 6.2.29): a character set byte and 11 characters. Only character set 1
/// (ASCII) is produced. Text is truncated to 11 characters and padded with zero bytes; characters outside printable ASCII
/// (0x20-0x7E) become '?'. Decoding stops at the first zero byte.
/// </summary>
public static class EntityMarkingText
{
    public const int Capacity = 11;
    public const byte AsciiCharacterSet = 1;

    /// <summary>The 11 marking bytes of a text: ASCII, truncated, zero padded.</summary>
    public static byte[] Encode(string? text)
    {
        var bytes = new byte[Capacity];
        if (string.IsNullOrEmpty(text))
        {
            return bytes;
        }

        int count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == Capacity)
            {
                break;
            }

            bytes[count++] = rune.Value is >= 0x20 and <= 0x7E ? (byte)rune.Value : (byte)'?';
        }

        return bytes;
    }

    /// <summary>The text of marking bytes: up to the first zero byte, bytes outside printable ASCII shown as '?'.</summary>
    public static string Decode(byte[]? characters)
    {
        if (characters is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Capacity);
        for (int i = 0; i < characters.Length && i < Capacity; i++)
        {
            byte b = characters[i];
            if (b == 0)
            {
                break;
            }

            builder.Append(b is >= 0x20 and <= 0x7E ? (char)b : '?');
        }

        return builder.ToString();
    }

    /// <summary>Writes a text into a marking record (character set ASCII).</summary>
    public static void Set(EntityMarking marking, string? text)
    {
        ArgumentNullException.ThrowIfNull(marking);
        marking.CharacterSet = AsciiCharacterSet;
        marking.Characters = Encode(text);
    }

    /// <summary>Reads the text of a marking record; an empty string when the character set is not ASCII.</summary>
    public static string Get(EntityMarking marking)
    {
        ArgumentNullException.ThrowIfNull(marking);
        return marking.CharacterSet == AsciiCharacterSet ? Decode(marking.Characters) : string.Empty;
    }
}
