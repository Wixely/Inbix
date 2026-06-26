using System.Security.Cryptography;
using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Core.Identities;

/// <summary>
/// Offline identity generator — draws from embedded curated pools (no external service, in keeping with
/// Inbix never calling out). Names and streets are shared across the supported English-language
/// countries; each country (see <see cref="Countries"/>) supplies its own cities/regions, phone format
/// and postcode format. Produces believable registration details: name, dictionary-word username,
/// strong password, address, adult date of birth, region-appropriate phone, and a security Q&amp;A.
/// </summary>
public sealed class RandomIdentityGenerator : IIdentityGenerator
{
    public Identity Generate(GenerateOptions options)
    {
        var country = PickCountry(options);
        var male = Random.Shared.Next(2) == 0;
        var first = Pick(male ? MaleFirst : FemaleFirst);
        var last = Pick(Surnames);
        var (city, region) = Pick(country.Cities);
        var (question, answer) = RandomSecurity(country.Cities);

        return new Identity
        {
            Country = country.Code,
            Gender = male ? "male" : "female",
            Title = male ? "Mr" : Pick(["Ms", "Mrs", "Miss"]),
            FirstName = first,
            LastName = last,
            Username = UsernameGenerator.Generate(),
            Password = RandomPassword(),
            DateOfBirth = RandomDateOfBirth(),
            Phone = RandomPhone(country.Code),
            Street = $"{Random.Shared.Next(1, 250)} {Pick(StreetNames)} {Pick(StreetTypes)}",
            City = city,
            StateCounty = region,
            Postcode = RandomPostcode(country.Code),
            SecurityQuestion = question,
            SecurityAnswer = answer,
        };
    }

    public string NewPassword() => RandomPassword();

    public string NewUsername() => UsernameGenerator.Generate();

    private static Country PickCountry(GenerateOptions options)
    {
        var enabled = (options.Countries ?? [])
            .Select(c => c?.Trim().ToLowerInvariant())
            .Where(c => !string.IsNullOrEmpty(c) && CountriesByCode.ContainsKey(c!))
            .Select(c => c!)
            .Distinct()
            .ToList();

        if (enabled.Count == 0)
            enabled = Countries.DefaultCodes.Where(CountriesByCode.ContainsKey).ToList();

        return CountriesByCode[enabled[Random.Shared.Next(enabled.Count)]];
    }

    private static T Pick<T>(IReadOnlyList<T> items) => items[Random.Shared.Next(items.Count)];

    private static DateOnly RandomDateOfBirth()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = Random.Shared.Next(18, 76); // 18..75
        return today.AddYears(-age).AddDays(-Random.Shared.Next(0, 365));
    }

    // ---- Region-specific formats ---------------------------------------------------------------

    private static string RandomPhone(string code) => code switch
    {
        "uk" => $"+44 7{Random.Shared.Next(100, 1000)} {Random.Shared.Next(100000, 1000000)}",
        "ie" => $"+353 8{Random.Shared.Next(0, 10)} {Random.Shared.Next(100, 1000)} {Random.Shared.Next(1000, 10000)}",
        "au" => $"+61 4{Random.Shared.Next(10, 100)} {Random.Shared.Next(100, 1000)} {Random.Shared.Next(100, 1000)}",
        "nz" => $"+64 2{Random.Shared.Next(0, 10)} {Random.Shared.Next(100, 1000)} {Random.Shared.Next(1000, 10000)}",
        "za" => $"+27 8{Random.Shared.Next(0, 10)} {Random.Shared.Next(100, 1000)} {Random.Shared.Next(1000, 10000)}",
        // us, ca and any fallback use the North American format.
        _ => $"+1 ({Random.Shared.Next(200, 1000)}) {Random.Shared.Next(200, 1000)}-{Random.Shared.Next(0, 10000):D4}"
    };

    private static string RandomPostcode(string code) => code switch
    {
        "uk" => UkPostcode(),
        "ca" => $"{Letter()}{Digit()}{Letter()} {Digit()}{Letter()}{Digit()}",        // A1A 1A1
        "ie" => $"{Letter()}{Digit()}{Digit()} {Alnum()}{Alnum()}{Alnum()}{Alnum()}", // Eircode, e.g. D02 X285
        "us" => Random.Shared.Next(10000, 100000).ToString(),                          // 5-digit ZIP
        _ => Random.Shared.Next(1000, 10000).ToString()                               // au/nz/za: 4 digits
    };

    private static string UkPostcode()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Random.Shared.Next(1, 3); i++) sb.Append(Letter());
        sb.Append(Random.Shared.Next(1, 30));
        sb.Append(' ').Append(Random.Shared.Next(0, 10)).Append(Letter()).Append(Letter());
        return sb.ToString();
    }

    private const string PostLetters = "ABCDEFGHJKLMNPRSTUVWXY"; // avoid easily-confused letters
    private static char Letter() => PostLetters[Random.Shared.Next(PostLetters.Length)];
    private static char Digit() => (char)('0' + Random.Shared.Next(10));
    private static char Alnum() => Random.Shared.Next(2) == 0 ? Letter() : Digit();

    private static string RandomPassword()
    {
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+";
        const string all = lower + upper + digits + symbols;

        var len = RandomNumberGenerator.GetInt32(14, 17); // 14..16
        var chars = new char[len];
        chars[0] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[1] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < len; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        for (var i = len - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    private static (string question, string answer) RandomSecurity((string City, string Region)[] cities)
    {
        var q = Random.Shared.Next(SecurityQuestions.Length);
        var answer = q switch
        {
            0 => Pick(Surnames),     // mother's maiden name
            1 => Pick(Pets),         // first pet
            2 => Pick(Schools),      // first school
            3 => Pick(cities).City,  // town born
            4 => Pick(MaleFirst),    // best friend
            5 => Pick(Cars),         // first car
            6 => Pick(FemaleFirst),  // favourite teacher
            _ => Pick(Foods)         // favourite food
        };
        return (SecurityQuestions[q], answer);
    }

    // ---- Per-country city/region data ----------------------------------------------------------

    private sealed class Country
    {
        public required string Code { get; init; }
        public required (string City, string Region)[] Cities { get; init; }
    }

    private static readonly Country[] CountryData =
    [
        new()
        {
            Code = "us",
            Cities =
            [
                ("New York", "NY"), ("Los Angeles", "CA"), ("Chicago", "IL"), ("Houston", "TX"),
                ("Phoenix", "AZ"), ("Philadelphia", "PA"), ("San Antonio", "TX"), ("San Diego", "CA"),
                ("Dallas", "TX"), ("Austin", "TX"), ("Seattle", "WA"), ("Denver", "CO"), ("Boston", "MA"),
                ("Nashville", "TN"), ("Portland", "OR"), ("Las Vegas", "NV"), ("Atlanta", "GA"),
                ("Miami", "FL"), ("Minneapolis", "MN"), ("Raleigh", "NC")
            ]
        },
        new()
        {
            Code = "uk",
            Cities =
            [
                ("London", "Greater London"), ("Manchester", "Greater Manchester"),
                ("Birmingham", "West Midlands"), ("Leeds", "West Yorkshire"), ("Liverpool", "Merseyside"),
                ("Sheffield", "South Yorkshire"), ("Bristol", "Bristol"), ("Newcastle", "Tyne and Wear"),
                ("Nottingham", "Nottinghamshire"), ("Leicester", "Leicestershire"), ("Brighton", "East Sussex"),
                ("Southampton", "Hampshire"), ("Oxford", "Oxfordshire"), ("Cambridge", "Cambridgeshire"),
                ("York", "North Yorkshire"), ("Cardiff", "South Glamorgan"), ("Edinburgh", "Midlothian"),
                ("Glasgow", "Lanarkshire"), ("Aberdeen", "Aberdeenshire"), ("Belfast", "County Antrim")
            ]
        },
        new()
        {
            Code = "ie",
            Cities =
            [
                ("Dublin", "Leinster"), ("Cork", "Munster"), ("Galway", "Connacht"), ("Limerick", "Munster"),
                ("Waterford", "Munster"), ("Kilkenny", "Leinster"), ("Sligo", "Connacht"),
                ("Wexford", "Leinster"), ("Drogheda", "Leinster"), ("Dundalk", "Leinster"),
                ("Bray", "Leinster"), ("Navan", "Leinster"), ("Ennis", "Munster"), ("Tralee", "Munster"),
                ("Athlone", "Leinster"), ("Letterkenny", "Ulster")
            ]
        },
        new()
        {
            Code = "ca",
            Cities =
            [
                ("Toronto", "ON"), ("Montreal", "QC"), ("Vancouver", "BC"), ("Calgary", "AB"),
                ("Ottawa", "ON"), ("Edmonton", "AB"), ("Winnipeg", "MB"), ("Halifax", "NS"),
                ("Victoria", "BC"), ("Hamilton", "ON"), ("Kitchener", "ON"), ("Saskatoon", "SK"),
                ("Regina", "SK"), ("St. John's", "NL"), ("Kelowna", "BC")
            ]
        },
        new()
        {
            Code = "au",
            Cities =
            [
                ("Sydney", "NSW"), ("Melbourne", "VIC"), ("Brisbane", "QLD"), ("Perth", "WA"),
                ("Adelaide", "SA"), ("Gold Coast", "QLD"), ("Canberra", "ACT"), ("Newcastle", "NSW"),
                ("Hobart", "TAS"), ("Darwin", "NT"), ("Wollongong", "NSW"), ("Geelong", "VIC"),
                ("Cairns", "QLD"), ("Townsville", "QLD"), ("Ballarat", "VIC")
            ]
        },
        new()
        {
            Code = "nz",
            Cities =
            [
                ("Auckland", "Auckland"), ("Wellington", "Wellington"), ("Christchurch", "Canterbury"),
                ("Hamilton", "Waikato"), ("Tauranga", "Bay of Plenty"), ("Dunedin", "Otago"),
                ("Palmerston North", "Manawatu"), ("Napier", "Hawke's Bay"), ("Nelson", "Nelson"),
                ("Rotorua", "Bay of Plenty"), ("New Plymouth", "Taranaki"), ("Whangarei", "Northland"),
                ("Invercargill", "Southland"), ("Queenstown", "Otago")
            ]
        },
        new()
        {
            Code = "za",
            Cities =
            [
                ("Johannesburg", "Gauteng"), ("Cape Town", "Western Cape"), ("Durban", "KwaZulu-Natal"),
                ("Pretoria", "Gauteng"), ("Gqeberha", "Eastern Cape"), ("Bloemfontein", "Free State"),
                ("East London", "Eastern Cape"), ("Pietermaritzburg", "KwaZulu-Natal"),
                ("Polokwane", "Limpopo"), ("Nelspruit", "Mpumalanga"), ("Kimberley", "Northern Cape"),
                ("George", "Western Cape"), ("Stellenbosch", "Western Cape"), ("Rustenburg", "North West")
            ]
        }
    ];

    private static readonly Dictionary<string, Country> CountriesByCode =
        CountryData.ToDictionary(c => c.Code);

    // ---- Shared pools --------------------------------------------------------------------------

    private static readonly string[] SecurityQuestions =
    [
        "What is your mother's maiden name?",
        "What was the name of your first pet?",
        "What was the name of your first school?",
        "In what town were you born?",
        "What is the first name of your best friend?",
        "What was the make and model of your first car?",
        "What was the first name of your favourite teacher?",
        "What is your favourite food?"
    ];

    private static readonly string[] Pets =
    [
        "Bella", "Max", "Charlie", "Lucy", "Cooper", "Luna", "Buddy", "Daisy", "Rocky", "Molly",
        "Bailey", "Milo", "Teddy", "Ruby", "Oscar", "Coco", "Toby", "Lola", "Murphy", "Pepper",
        "Shadow", "Ginger", "Smokey", "Biscuit", "Patch"
    ];

    private static readonly string[] Cars =
    [
        "Ford Focus", "Vauxhall Corsa", "Volkswagen Golf", "Toyota Corolla", "Honda Civic",
        "Nissan Micra", "Renault Clio", "Peugeot 208", "Ford Fiesta", "Mini Cooper",
        "Hyundai i30", "Mazda 3", "Chevrolet Malibu", "Jeep Wrangler", "Subaru Impreza", "Holden Commodore"
    ];

    private static readonly string[] Foods =
    [
        "Pizza", "Sushi", "Lasagne", "Curry", "Tacos", "Burgers", "Pasta", "Roast dinner",
        "Fish and chips", "Ramen", "Pad Thai", "Steak", "Risotto", "Paella", "Burrito", "Biltong"
    ];

    private static readonly string[] MaleFirst =
    [
        "James", "Oliver", "Harry", "Jack", "George", "Noah", "Charlie", "Jacob", "Thomas", "Oscar",
        "William", "Henry", "Leo", "Alfie", "Joshua", "Freddie", "Archie", "Logan", "Theo", "Arthur",
        "Mason", "Daniel", "Edward", "Samuel", "Joseph", "Max", "Lucas", "Ethan", "Alexander", "Benjamin",
        "Sebastian", "Harrison", "Dylan", "Callum", "Liam", "Nathan", "Ryan", "Aaron", "Connor", "Jamie",
        "Elijah", "Michael", "Jackson", "Aiden", "Matthew", "David", "Carter", "Owen", "Wyatt", "John",
        "Luke", "Jayden", "Grayson", "Levi", "Isaac", "Gabriel", "Julian", "Anthony", "Andrew", "Adam",
        "Cameron", "Jordan", "Patrick", "Sean", "Cian", "Conor", "Declan", "Niall", "Finn", "Rory",
        "Hamish", "Lachlan", "Hunter", "Cooper", "Blake", "Riley", "Angus", "Tane", "Sipho", "Thabo"
    ];

    private static readonly string[] FemaleFirst =
    [
        "Olivia", "Amelia", "Isla", "Ava", "Emily", "Sophia", "Grace", "Mia", "Poppy", "Ella",
        "Lily", "Charlotte", "Evie", "Sophie", "Isabella", "Freya", "Daisy", "Phoebe", "Florence", "Alice",
        "Jessica", "Ruby", "Chloe", "Holly", "Lucy", "Emma", "Hannah", "Megan", "Eleanor", "Maisie",
        "Imogen", "Bethany", "Abigail", "Molly", "Scarlett", "Rosie", "Niamh", "Erin", "Katie", "Zoe",
        "Harper", "Evelyn", "Avery", "Sofia", "Madison", "Victoria", "Camila", "Penelope", "Layla", "Nora",
        "Zoey", "Ellie", "Addison", "Natalie", "Brooklyn", "Hailey", "Savannah", "Aria", "Aubrey", "Stella",
        "Allison", "Saoirse", "Aoife", "Ciara", "Maeve", "Orla", "Sinead", "Aria", "Mila", "Hazel",
        "Willow", "Aroha", "Mere", "Thandi", "Lerato", "Zara", "Maya", "Anya", "Priya", "Aaliyah"
    ];

    private static readonly string[] Surnames =
    [
        "Smith", "Jones", "Williams", "Taylor", "Brown", "Davies", "Evans", "Wilson", "Thomas", "Roberts",
        "Johnson", "Lewis", "Walker", "Robinson", "Wood", "Thompson", "White", "Watson", "Jackson", "Wright",
        "Green", "Harris", "Cooper", "King", "Lee", "Martin", "Clarke", "James", "Morgan", "Hughes",
        "Edwards", "Hill", "Moore", "Clark", "Harrison", "Scott", "Young", "Morris", "Hall", "Ward",
        "Turner", "Carter", "Phillips", "Mitchell", "Patel", "Adams", "Campbell", "Anderson", "Allen", "Bell",
        "Garcia", "Miller", "Davis", "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Perez",
        "Sanchez", "Ramirez", "Torres", "Flores", "Nelson", "Baker", "Rivera", "Murphy", "Kelly", "Byrne",
        "Ryan", "Walsh", "McCarthy", "Gallagher", "Doyle", "Kennedy", "Lynch", "Murray", "Quinn", "Brennan",
        "Singh", "Kaur", "Nguyen", "Chen", "Wang", "Kim", "Naidoo", "Botha", "Pillay", "Dlamini",
        "Mokoena", "Williamson", "Fraser", "Stewart", "Cameron", "Ferguson", "Reid", "Graham", "Wallace", "Bennett"
    ];

    private static readonly string[] StreetNames =
    [
        "High", "Station", "Church", "Victoria", "Main", "Park", "Mill", "Queens", "Kings", "New",
        "Manor", "School", "North", "Green", "Springfield", "George", "Albert", "Bridge", "Grange", "Windsor",
        "York", "Chapel", "Meadow", "Oak", "Elm", "Highfield", "Woodland", "Riverside", "Castle", "Abbey",
        "Pine", "Maple", "Cedar", "Washington", "Lake", "Hill", "Walnut", "Spring", "Highland", "Sunset",
        "Lincoln", "River", "Forest", "Madison", "Jefferson", "Franklin", "Willow", "Birch", "Chestnut", "Ridge",
        "Valley", "Jackson", "Liberty", "Union", "College", "Prospect", "Cherry", "Hillside", "Garden", "Orchard",
        "Sycamore", "Linden", "Magnolia", "Acacia", "Wattle", "Banksia", "Beach", "Harbour", "Mountain", "Vista"
    ];

    private static readonly string[] StreetTypes =
    [
        "Street", "Road", "Avenue", "Lane", "Drive", "Way", "Close", "Court", "Place", "Terrace",
        "Crescent", "Grove", "Parade", "Boulevard"
    ];

    private static readonly string[] Schools =
    [
        "St Mary's School", "Greenfield High School", "Oakwood Academy", "Victoria Primary School",
        "Park View School", "Riverside College", "Hillcrest Academy", "St John's School",
        "Meadow Primary School", "Kingsway High School", "Brookfield School", "Highfield Academy",
        "The Grove School", "Lincoln High School", "Washington Elementary", "Roosevelt Middle School",
        "Kennedy High School", "Westfield School", "Lakeview High School", "Sacred Heart School"
    ];
}
