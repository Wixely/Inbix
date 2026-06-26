namespace Inbix.Core.Identities;

/// <summary>
/// Builds memorable, random usernames by mixing curated dictionary words (adjective / noun / verb) with
/// an optional number and a varied style — e.g. <c>golden_chase92</c>, <c>GoldenChase</c>,
/// <c>swiftFalcon7</c>, <c>river.otter2024</c>, or a single word like <c>Voyager88</c>. The word lists
/// are authored in-repo (no third-party corpus), so there is nothing extra to license. Extend the
/// arrays below to add variety.
/// </summary>
internal static class UsernameGenerator
{
    public static string Generate()
    {
        var words = PickWords();
        var numberChance = words.Length == 1 ? 9 : 7; // single words almost always carry a number
        var number = Random.Shared.Next(10) < numberChance ? RandomNumber() : string.Empty;
        return ApplyStyle(words, number);
    }

    private static string[] PickWords() => Random.Shared.Next(10) switch
    {
        <= 3 => [Pick(Adjectives), Pick(Nouns)],                 // golden chase  (most common)
        4 => [Pick(Nouns)],                                      // single noun:      falcon
        5 => [Pick(Adjectives)],                                 // single adjective: crimson
        6 => [Pick(Verbs)],                                      // single verb:      voyage
        7 => [Pick(Nouns), Pick(Nouns)],                         // river otter
        8 => [Pick(Verbs), Pick(Nouns)],                         // chase comet
        _ => [Pick(Adjectives), Pick(Adjectives), Pick(Nouns)]   // swift golden falcon
    };

    private static string RandomNumber() => Random.Shared.Next(5) switch
    {
        0 => Random.Shared.Next(1, 10).ToString(),               // 1 digit
        1 or 2 => Random.Shared.Next(10, 100).ToString(),        // 2 digits (e.g. 92)
        3 => Random.Shared.Next(1980, 2025).ToString(),          // year-ish
        _ => Random.Shared.Next(100, 1000).ToString()            // 3 digits
    };

    private static string ApplyStyle(string[] words, string number) => Random.Shared.Next(6) switch
    {
        0 => string.Join("_", words) + number,                       // golden_chase92
        1 => string.Concat(words.Select(Capitalize)) + number,       // GoldenChase92  (PascalCase)
        2 => Camel(words) + number,                                  // goldenChase92  (camelCase)
        3 => string.Concat(words) + number,                          // goldenchase92  (flat)
        4 => string.Join(".", words) + number,                       // golden.chase92
        _ => string.Join("_", words.Select(Capitalize)) + number     // Golden_Chase92
    };

    private static string Camel(string[] words) => words[0] + string.Concat(words.Skip(1).Select(Capitalize));

    private static string Capitalize(string w) => char.ToUpperInvariant(w[0]) + w[1..];

    private static string Pick(string[] items) => items[Random.Shared.Next(items.Length)];

    // All lowercase, single words; casing and separators are applied per style above.
    private static readonly string[] Adjectives =
    [
        "swift", "golden", "silent", "brave", "clever", "mighty", "gentle", "fierce", "calm", "bright",
        "dusky", "wild", "noble", "lucky", "cosmic", "electric", "frozen", "hidden", "ancient", "modern",
        "rapid", "silky", "velvet", "crimson", "azure", "amber", "scarlet", "cobalt", "ivory", "bold",
        "quiet", "lively", "sunny", "stormy", "misty", "dusty", "rusty", "shiny", "glossy", "sleek",
        "rugged", "smooth", "sharp", "fuzzy", "cozy", "breezy", "frosty", "snowy", "eager", "jolly",
        "merry", "witty", "keen", "vivid", "mellow", "groovy", "funky", "nifty", "dapper", "classy",
        "fancy", "plucky", "spunky", "zesty", "peppy", "snappy", "quirky", "cheeky", "dreamy", "hazy",
        "lucid", "vast", "tiny", "grand", "humble", "royal", "feral", "primal", "lunar", "solar",
        "stellar", "astral", "mystic", "arcane", "sacred", "secret", "stealthy", "nimble", "agile",
        "sturdy", "robust", "hardy", "fearless", "tireless", "timeless", "endless", "boundless",
        "gilded", "polished", "radiant", "gleaming", "glowing", "blazing", "roaring", "soaring",
        "dashing", "daring", "crystal", "marble", "copper", "bronze", "silver", "emerald", "ruby",
        "sapphire", "coral", "teal", "indigo", "violet", "olive", "hazel", "sandy", "autumn", "winter",
        "summer", "wily", "brisk", "spry", "jade", "onyx",
        // --- expanded ---
        "cheery", "chipper", "dandy", "jaunty", "bubbly", "sparkly", "twinkly", "glinting", "shadowed",
        "moonlit", "sunlit", "starlit", "twilight", "frosted", "glazed", "dappled", "mottled", "speckled",
        "striped", "spotted", "woolly", "fluffy", "downy", "plush", "satin", "suede", "leathery", "glassy",
        "pearly", "prismatic", "iridescent", "luminous", "lustrous", "burnished", "wispy", "willowy",
        "lithe", "supple", "springy", "bouncy", "zippy", "jumpy", "frisky", "giddy", "perky", "sprightly",
        "chirpy", "gleeful", "blithe", "buoyant", "jovial", "genial", "affable", "amiable", "cordial",
        "hearty", "earnest", "ardent", "fervent", "zealous", "spirited", "gallant", "valiant", "heroic",
        "dauntless", "intrepid", "undaunted", "staunch", "steadfast", "stalwart", "resolute", "dogged",
        "tenacious", "scrappy", "feisty", "gutsy", "rowdy", "jazzy", "snazzy", "swanky", "ritzy", "posh",
        "regal", "lordly", "stately", "lofty", "towering", "colossal", "titanic", "gigantic", "mammoth",
        "jumbo", "petite", "dainty", "dinky", "teeny", "svelte", "trim", "neat", "tidy", "crisp", "fresh",
        "dewy", "verdant", "lush", "leafy", "mossy", "woodsy", "earthy", "loamy", "rocky", "craggy",
        "alpine", "boreal", "arctic", "tropic", "coastal", "tidal", "briny", "salty", "oaken", "brassy",
        "smoky", "minty", "peachy", "lemony", "nutty", "cherry", "plummy"
    ];

    private static readonly string[] Nouns =
    [
        "falcon", "otter", "badger", "fox", "wolf", "bear", "lynx", "hawk", "eagle", "raven",
        "sparrow", "robin", "finch", "heron", "crane", "owl", "swan", "lark", "wren", "dove",
        "tiger", "lion", "panther", "jaguar", "leopard", "cougar", "puma", "bison", "moose", "elk",
        "stag", "deer", "hare", "beaver", "marten", "weasel", "ferret", "mongoose", "meerkat", "koala",
        "panda", "gecko", "iguana", "cobra", "viper", "python", "mamba", "dragon", "phoenix", "griffin",
        "comet", "meteor", "nebula", "quasar", "pulsar", "galaxy", "nova", "cosmos", "orbit", "ember",
        "cinder", "flame", "spark", "blaze", "frost", "glacier", "tundra", "summit", "canyon", "ridge",
        "mesa", "delta", "fjord", "reef", "lagoon", "harbor", "river", "brook", "creek", "meadow",
        "prairie", "thicket", "grove", "willow", "cedar", "birch", "maple", "aspen", "alder", "cypress",
        "juniper", "bramble", "fern", "clover", "thistle", "sage", "ginger", "saffron", "anchor", "beacon",
        "lantern", "compass", "rudder", "pebble", "boulder", "flint", "quartz", "opal", "garnet", "topaz",
        "arrow", "lance", "saber", "shield", "helm", "crown", "banner", "torch", "forge", "anvil",
        "hammer", "chisel", "quill", "scroll", "cipher", "rune", "totem", "relic", "oracle", "sphinx",
        "titan", "atlas", "hydra", "kraken", "wyvern", "sprite", "nomad", "ranger", "scout", "pilot",
        "sailor", "voyager", "drifter", "rover", "ronin", "warden", "sentry", "herald", "minstrel", "bard",
        // --- expanded ---
        "stoat", "vole", "lemur", "tapir", "ocelot", "serval", "caracal", "dingo", "jackal", "coyote",
        "hyena", "gazelle", "antelope", "impala", "oryx", "ibex", "walrus", "seal", "dolphin", "orca",
        "narwhal", "marlin", "tuna", "salmon", "trout", "pike", "koi", "minnow", "urchin", "starfish",
        "seahorse", "octopus", "squid", "nautilus", "lobster", "mantis", "beetle", "cricket", "cicada",
        "firefly", "dragonfly", "moth", "hornet", "magpie", "jay", "kestrel", "osprey", "condor", "ibis",
        "stork", "pelican", "puffin", "albatross", "kingfisher", "woodpecker", "nightingale", "starling",
        "swallow", "hummingbird", "toucan", "macaw", "parrot", "peacock", "pheasant", "quail", "grouse",
        "plover", "bayou", "marsh", "moor", "heath", "glen", "dale", "vale", "dell", "hollow", "knoll",
        "butte", "bluff", "cove", "inlet", "strait", "channel", "eddy", "dune", "oasis", "savanna",
        "steppe", "taiga", "jungle", "copse", "vineyard", "amethyst", "turquoise", "malachite", "lapis",
        "jasper", "agate", "beryl", "citrine", "peridot", "moonstone", "obsidian", "granite", "slate",
        "pyrite", "pewter", "pegasus", "chimera", "cerberus", "minotaur", "cyclops", "valkyrie", "golem",
        "manticore", "leviathan", "behemoth", "roc", "thunderbird", "kitsune", "naga", "nymph", "dryad",
        "faun", "centaur", "unicorn", "wisp", "aurora", "zephyr", "monsoon", "cyclone", "tempest",
        "vortex", "halo", "corona", "meridian", "horizon", "eclipse", "zenith", "solstice", "citadel",
        "bastion", "spire", "turret", "obelisk", "monolith", "cairn", "lighthouse", "windmill", "foundry",
        "kiln", "loom", "mallet", "sickle", "scythe", "trident", "rapier", "cutlass", "scimitar", "dagger",
        "longbow", "quiver", "gauntlet", "talisman", "amulet", "sigil", "glyph", "codex", "tome",
        "grimoire", "sextant", "astrolabe", "telescope", "mariner", "corsair", "wayfarer", "pilgrim",
        "pioneer", "vanguard", "courier", "envoy", "sentinel", "guardian", "paladin", "gladiator",
        "centurion", "archer", "lancer", "marksman", "falconer", "forager", "tinker", "mason", "cooper",
        "fletcher"
    ];

    private static readonly string[] Verbs =
    [
        "chase", "dash", "soar", "glide", "drift", "roam", "prowl", "dart", "leap", "bound",
        "sprint", "hover", "swoop", "dive", "climb", "scale", "forge", "craft", "build", "mend",
        "weave", "spin", "carve", "etch", "paint", "sketch", "dream", "wander", "explore", "seek",
        "hunt", "track", "gather", "harvest", "kindle", "ignite", "flicker", "gleam", "shimmer", "ripple",
        "surge", "flow", "cascade", "tumble", "scatter", "whisper", "echo", "rumble", "thunder", "glow",
        "float", "sail", "voyage", "journey", "venture", "ramble", "meander", "vault", "gallop", "trek",
        // --- expanded ---
        "amble", "saunter", "stride", "march", "parade", "prance", "frolic", "cavort", "romp", "scurry",
        "scamper", "skitter", "flit", "flutter", "swirl", "twirl", "whirl", "spiral", "braid", "stitch",
        "shape", "mold", "sculpt", "hew", "temper", "smolder", "simmer", "brew", "steep", "blend",
        "whisk", "churn", "knead", "probe", "survey", "chart", "trace", "pursue", "stalk", "pounce",
        "lunge", "parry", "dodge", "sparkle", "glisten", "twinkle", "dazzle", "radiate", "flare", "pulse",
        "quiver", "sway", "coast", "cruise", "traverse", "navigate", "embark", "ascend", "hoist", "hurl",
        "fling", "launch", "rocket", "zoom"
    ];
}
