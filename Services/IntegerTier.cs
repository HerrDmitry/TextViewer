namespace TextViewer.Services;

/// <summary>
/// Storage tier for segment pair values. Each tier uses the corresponding
/// unsigned integer type for BOTH values in every pair within the segment.
/// Tier is determined by the maximum Byte_Length in the segment
/// (since Char_Length ≤ Byte_Length, both values always fit).
/// </summary>
internal enum IntegerTier : byte
{
    Byte = 1,    // 1 byte per value, max 255
    UShort = 2,  // 2 bytes per value, max 65,535
    UInt = 4,    // 4 bytes per value, max 4,294,967,295
    ULong = 8    // 8 bytes per value, max 18,446,744,073,709,551,615
}
