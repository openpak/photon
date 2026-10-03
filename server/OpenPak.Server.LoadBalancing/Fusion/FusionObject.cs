namespace OpenPak.Server.LoadBalancing.Fusion;

/// <summary>
/// One networked object as the server stores it (spec §5, §8.1): the latest value of every 32-bit word
/// and the stamp of the send round in which each word last changed. Data words are opaque, except the
/// main-transform block (§4.7), which is kept in wire form in words 22-25 and re-emitted verbatim.
/// </summary>
public sealed class FusionObject
{
    public const int HeaderWords = 20;
    public const int WordCountWord = 1, TypeWord = 2, FlagsWord = 6, InputAuthorityWord = 7, StateAuthorityWord = 8, InterestWord = 9;
    public const int FlagGlobalObjectInterest = 1, FlagDestroyWhenStateAuthorityLeaves = 2, FlagHasMainTrsp = 8, FlagAllowStateAuthorityOverride = 16, FlagStruct = 32;
    public const int TransformFirstWord = 22, TransformLastWord = 28;
    /// <summary>Raw PlayerRef values (spec §6.1).</summary>
    public const int PlayerNone = 0, PlayerMasterClient = -1;

    public FusionObject(uint id, int wordCount)
    {
        Id = id;
        Words = new int[wordCount];
        Stamps = new int[wordCount];
        Words[0] = (int)id;
    }

    public uint Id { get; }
    /// <summary>The actor that created a client object, charged for its memory.</summary>
    public int Creator { get; init; }
    /// <summary>Position in the room's client-object list, for O(1) removal.</summary>
    internal int WorldIndex { get; set; }
    public int[] Words { get; }
    public int[] Stamps { get; }
    /// <summary>The highest of <see cref="Stamps"/>.</summary>
    public int ChangedStamp { get; private set; }

    public int WordCount => Words.Length;
    public int Flags => Words[FlagsWord];
    public int StateAuthority => Words[StateAuthorityWord];
    public bool IsStruct => (Flags & FlagStruct) != 0;
    public bool HasTransform => HasTransformBlock(Flags, WordCount);

    /// <summary>Whether words 22-28 travel as the transform block (spec §4.7).</summary>
    public static bool HasTransformBlock(int flags, int wordCount) => (flags & FlagHasMainTrsp) != 0 && wordCount >= 34;

    /// <summary>Stores <paramref name="value"/> and stamps the word if it changed.</summary>
    public void Set(int index, int value, int stamp)
    {
        if (Words[index] == value) return;
        Words[index] = value;
        Stamps[index] = stamp;
        if (stamp > ChangedStamp) ChangedStamp = stamp;
    }

    /// <summary>
    /// Writes the record body after the id delta (spec §4.6 fields 2-5) for a recipient. Selects every word
    /// stamped after <paramref name="since"/>; header word 9 carries <paramref name="interestFlags"/> and is
    /// only sent in a full header. Returns the number of changed words written, which the caller uses to
    /// drop an empty record.
    /// </summary>
    public int WriteRecordBody(NetBitWriter writer, bool full, int since, int interestFlags)
    {
        writer.WriteBoolean(full);
        var cursor = 0;
        if (full)
        {
            for (var index = 1; index < HeaderWords; index++)
            {
                var word = index == InterestWord ? interestFlags : Words[index];
                writer.WriteBoolean(word != 0);
                if (word != 0) FusionSimCodec.WriteWord(writer, word);
            }
            cursor = HeaderWords - 1;
        }

        var written = 0;
        var transform = HasTransform;
        for (var index = cursor + 1; index < WordCount; index++)
        {
            if (index == InterestWord) continue;   // per recipient; delivered with the full header
            if (transform && index == TransformFirstWord)
            {
                if (TransformChangedSince(since))
                {
                    writer.WriteUInt32VarLength((uint)(TransformFirstWord - cursor), 4);
                    for (var axis = 0; axis < 3; axis++)
                        writer.WriteUInt32VarLength(FusionSimCodec.ZigZag32(Words[TransformFirstWord + axis]), 4);
                    writer.WriteUInt32((uint)Words[TransformFirstWord + 3]);
                    cursor = TransformLastWord;
                    written++;
                }
                index = TransformLastWord;
                continue;
            }
            if (Stamps[index] <= since) continue;
            writer.WriteUInt32VarLength((uint)(index - cursor), 4);
            FusionSimCodec.WriteWord(writer, Words[index]);
            cursor = index;
            written++;
        }
        writer.WriteUInt32VarLength(0, 4);
        return written;
    }

    private bool TransformChangedSince(int since)
    {
        for (var index = TransformFirstWord; index <= TransformLastWord; index++)
            if (Stamps[index] > since) return true;
        return false;
    }
}
