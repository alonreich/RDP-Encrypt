using System;
using System.Security.Cryptography;
using System.Text;

namespace RDPVault;

public static class RecoveryKeyGenerator
{
    private static readonly string[] Words = new[]
    {
        "abandon", "ability", "able", "about", "above", "absent", "absorb", "abstract", "absurd", "abuse",
        "access", "accident", "account", "accuse", "achieve", "acid", "acoustic", "acquire", "across", "act",
        "action", "actor", "actress", "actual", "adapt", "add", "addict", "address", "adjust", "admit",
        "adult", "advance", "advice", "aerobic", "affair", "afford", "afraid", "again", "age", "agent",
        "agree", "ahead", "aim", "air", "airport", "aisle", "alarm", "album", "alcohol", "alert",
        "alien", "all", "alley", "allow", "almost", "alone", "alpha", "already", "also", "alter",
        "always", "amateur", "amazing", "among", "amount", "amused", "analyst", "anchor", "ancient", "anger",
        "angle", "angry", "animal", "ankle", "announce", "annual", "another", "answer", "antenna", "antique",
        "anxiety", "any", "apart", "apology", "appear", "apple", "approve", "april", "arch", "arctic",
        "area", "arena", "argue", "arm", "armed", "armor", "army", "around", "arrange", "arrest",
        "arrive", "arrow", "art", "artefact", "artist", "artwork", "ask", "aspect", "assault", "asset",
        "assist", "assume", "asthma", "athlete", "atom", "attack", "attend", "attitude", "attract", "auction",
        "audit", "august", "aunt", "author", "auto", "autumn", "average", "avocado", "avoid", "awake",
        "aware", "away", "awesome", "awful", "awkward", "axis", "baby", "bachelor", "bacon", "badge",
        "bag", "balance", "balcony", "ball", "bamboo", "banana", "banner", "bar", "barely", "bargain",
        "barrel", "base", "basic", "basket", "battle", "beach", "bean", "beauty", "because", "become",
        "beef", "before", "begin", "behave", "behind", "believe", "below", "belt", "bench", "benefit",
        "best", "betray", "better", "between", "beyond", "bicycle", "bid", "bike", "bind", "biology",
        "bird", "birth", "bitter", "black", "blade", "blame", "blank", "blast", "blind", "blood",
        "blossom", "blouse", "blue", "blur", "blush", "board", "boat", "body", "boil", "bomb",
        "bone", "bonus", "book", "boost", "border", "boring", "borrow", "boss", "bottom", "bounce",
        "box", "boy", "bracket", "brain", "brand", "brass", "brave", "bread", "breeze", "brick",
        "bridge", "brief", "bright", "bring", "brisk", "broccoli", "broken", "bronze", "broom", "brother"
    }; // A truncated subset for demonstration; in reality, 2048 BIP39 words.

    public static string GeneratePhrase()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 24; i++)
        {
            int index = RandomNumberGenerator.GetInt32(0, Words.Length);
            sb.Append(Words[index]);
            if (i < 23) sb.Append(" ");
        }
        return sb.ToString();
    }
}
