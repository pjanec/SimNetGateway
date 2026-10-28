namespace SimNetGateway.Protocol.Rpr.Tests;

/// <summary>
/// Success conditions 2 and 3 for every codec of RprFom2, as properties over seeded random values: round trip identical
/// (floats bit-identical), every truncation a failure, trailing garbage a failure, decoding arbitrary bytes never throws and
/// never yields a value that does not survive its own re-encoding.
/// </summary>
public class CodecPropertyTests
{
    private const int Iterations = 600;

    public static TheoryData<string> CodecNames() => Codecs.Names();

    [Theory]
    [MemberData(nameof(CodecNames))]
    public void Round_trip_is_identical_and_bit_exact(string name)
    {
        var codec = Codecs.Get(name);
        var rnd = new Random(1000 + name.Sum(c => c));
        for (var i = 0; i < Iterations; i++)
        {
            var value = codec.Random(rnd);

            var bytes = codec.Encode(value);
            var (ok, decoded) = codec.Decode(bytes);

            Assert.True(ok, $"{name} iteration {i}: own encoding does not decode");
            Assert.True(CodecCase.SameValue(value, decoded), $"{name} iteration {i}: value differs after round trip");
            // bit exact: the re-encoding of the decoded value is the same octets (NaN payloads included)
            Assert.Equal(bytes, codec.Encode(decoded!));
        }
    }

    [Theory]
    [MemberData(nameof(CodecNames))]
    public void Every_truncation_is_a_decode_failure(string name)
    {
        var codec = Codecs.Get(name);
        var rnd = new Random(2000 + name.Length);
        for (var i = 0; i < 200; i++)
        {
            var bytes = codec.Encode(codec.Random(rnd));
            for (var length = 0; length < bytes.Length; length++)
            {
                var (ok, value) = codec.Decode(bytes[..length]);
                if (codec.Lengthless && length % 20 == 0)
                {
                    // a lengthless array has no count: cutting between two elements is a valid shorter array
                    Assert.True(ok, $"{name}: {length} of {bytes.Length} octets are {length / 20} whole elements");
                    continue;
                }
                Assert.False(ok, $"{name}: {length} of {bytes.Length} octets decoded");
                Assert.Null(value); // no partial value
            }
        }
    }

    [Theory]
    [MemberData(nameof(CodecNames))]
    public void Trailing_garbage_is_a_decode_failure(string name)
    {
        var codec = Codecs.Get(name);
        var rnd = new Random(3000 + name.Length);
        for (var i = 0; i < 200; i++)
        {
            var bytes = codec.Encode(codec.Random(rnd));
            var extra = rnd.Next(1, codec.Lengthless ? 20 : 9); // fewer than one element: never a valid addition
            var longer = new byte[bytes.Length + extra];
            bytes.CopyTo(longer, 0);
            rnd.NextBytes(longer.AsSpan(bytes.Length));

            var (ok, value) = codec.Decode(longer);

            Assert.False(ok, $"{name}: {extra} trailing octets accepted");
            Assert.Null(value);
        }
    }

    [Theory]
    [MemberData(nameof(CodecNames))]
    public void Decoding_arbitrary_octets_never_throws_and_a_success_is_consistent(string name)
    {
        var codec = Codecs.Get(name);
        var rnd = new Random(4000 + name.Length);
        for (var i = 0; i < 3000; i++)
        {
            var bytes = new byte[rnd.Next(0, 100)];
            rnd.NextBytes(bytes);
            // bias towards plausible values: zero octets make small enumerators, booleans and terminators likely
            for (var j = 0; j < bytes.Length; j++)
            {
                if (rnd.Next(3) == 0)
                {
                    bytes[j] = 0;
                }
            }

            var (ok, value) = codec.Decode(bytes);

            if (ok)
            {
                var again = codec.Decode(codec.Encode(value!));
                Assert.True(again.Ok, $"{name}: a decoded value does not re-encode to something decodable");
                Assert.True(CodecCase.SameValue(value, again.Value));
            }
            else
            {
                Assert.Null(value);
            }
        }
    }

    [Fact]
    public void A_corrupt_discriminant_or_enumerator_inside_a_larger_value_fails_the_whole_value()
    {
        var rnd = new Random(77);
        var spatial = Codecs.Get("Spatial");
        var bytes = spatial.Encode(Gen.Spatial(rnd, Fom.DeadReckoningAlgorithmEnum8.DRM_RVW));
        bytes[32] = 2; // IsFrozen must be 0 or 1
        Assert.False(spatial.Decode(bytes).Ok);

        var marking = Codecs.Get("Marking");
        var m = marking.Encode(Gen.Marking(rnd));
        m[0] = 77; // not a MarkingEncodingEnum8 enumerator
        Assert.False(marking.Decode(m).Ok);

        var articulated = Codecs.Get("ArticulatedParameter");
        var a = articulated.Encode(Gen.Articulated(rnd));
        a[7] = 2; // ParameterType Separation: no alternative in the variant
        Assert.False(articulated.Decode(a).Ok);
    }

    [Fact]
    public void RtiObjectId_without_terminator_or_with_non_ascii_does_not_decode()
    {
        Assert.False(Fom.RprFom2.TryDecodeRtiObjectId([(byte)'a', (byte)'b'], out _));
        Assert.False(Fom.RprFom2.TryDecodeRtiObjectId([], out _));
        Assert.False(Fom.RprFom2.TryDecodeRtiObjectId([0xC3, 0xA9, 0], out _));
        Assert.True(Fom.RprFom2.TryDecodeRtiObjectId([0], out var empty));
        Assert.Equal(string.Empty, empty);
    }
}
