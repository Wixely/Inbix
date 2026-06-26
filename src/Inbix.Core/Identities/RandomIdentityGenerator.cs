using System.Security.Cryptography;
using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Core.Identities;

/// <summary>
/// Offline identity generator — draws from embedded curated UK/US data pools (no external service, in
/// keeping with Inbix never calling out). Produces believable registration details: name, username,
/// strong password, address, adult date of birth, region-appropriate phone, and a security Q&amp;A.
/// </summary>
public sealed class RandomIdentityGenerator : IIdentityGenerator
{
    public Identity Generate(GenerateOptions options)
    {
        var useUk = options.IncludeUk;
        var useUs = options.IncludeUs;
        if (!useUk && !useUs) { useUk = true; useUs = true; } // never generate from an empty pool

        var country = (useUk && useUs)
            ? (Random.Shared.Next(2) == 0 ? "uk" : "us")
            : (useUk ? "uk" : "us");

        var data = country == "uk" ? Uk : Us;
        var male = Random.Shared.Next(2) == 0;
        var first = Pick(male ? data.MaleFirst : data.FemaleFirst);
        var last = Pick(data.Surnames);
        var (street, city, region, postcode) = RandomAddress(country, data);
        var (question, answer) = RandomSecurity(data);

        return new Identity
        {
            Country = country,
            Gender = male ? "male" : "female",
            Title = male ? "Mr" : Pick(["Ms", "Mrs", "Miss"]),
            FirstName = first,
            LastName = last,
            Username = RandomUsername(first, last),
            Password = RandomPassword(),
            DateOfBirth = RandomDateOfBirth(),
            Phone = RandomPhone(country),
            Street = street,
            City = city,
            StateCounty = region,
            Postcode = postcode,
            SecurityQuestion = question,
            SecurityAnswer = answer,
        };
    }

    public string NewPassword() => RandomPassword();

    public string NewUsername(string firstName, string lastName)
    {
        var first = string.IsNullOrWhiteSpace(firstName) ? "user" : firstName.Trim();
        var last = string.IsNullOrWhiteSpace(lastName) ? "name" : lastName.Trim();
        return RandomUsername(first, last);
    }

    private static T Pick<T>(IReadOnlyList<T> items) => items[Random.Shared.Next(items.Count)];

    private static DateOnly RandomDateOfBirth()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = Random.Shared.Next(18, 76); // 18..75
        return today.AddYears(-age).AddDays(-Random.Shared.Next(0, 365));
    }

    private static (string street, string city, string region, string postcode) RandomAddress(string country, LocaleData d)
    {
        var street = $"{Random.Shared.Next(1, 250)} {Pick(d.StreetNames)} {Pick(d.StreetTypes)}";
        var (city, region) = Pick(d.Cities);
        var postcode = country == "uk" ? UkPostcode() : UsZip();
        return (street, city, region, postcode);
    }

    private static string UkPostcode()
    {
        const string letters = "ABDEFGHJLNPQRSTUWXYZ";
        var sb = new StringBuilder();
        for (var i = 0; i < Random.Shared.Next(1, 3); i++) sb.Append(letters[Random.Shared.Next(letters.Length)]);
        sb.Append(Random.Shared.Next(1, 30));
        sb.Append(' ').Append(Random.Shared.Next(0, 10));
        sb.Append(letters[Random.Shared.Next(letters.Length)]).Append(letters[Random.Shared.Next(letters.Length)]);
        return sb.ToString();
    }

    private static string UsZip() => Random.Shared.Next(10000, 100000).ToString();

    private static string RandomPhone(string country)
    {
        if (country == "uk")
            return $"+44 7{Random.Shared.Next(100, 1000)} {Random.Shared.Next(100000, 1000000)}";
        // US: +1 (NXX) NXX-XXXX
        return $"+1 ({Random.Shared.Next(200, 1000)}) {Random.Shared.Next(200, 1000)}-{Random.Shared.Next(0, 10000):D4}";
    }

    private static string RandomUsername(string first, string last)
    {
        var f = first.ToLowerInvariant();
        var l = last.ToLowerInvariant();
        var num = Random.Shared.Next(0, 100);
        return Random.Shared.Next(6) switch
        {
            0 => $"{f}.{l}",
            1 => $"{f[..1]}{l}",
            2 => $"{f}{l}{num}",
            3 => $"{f}_{l}",
            4 => $"{l}{f[..1]}{num}",
            _ => $"{f}{num}"
        };
    }

    private static string RandomPassword()
    {
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*-_=+";
        const string all = lower + upper + digits + symbols;

        var len = RandomNumberGenerator.GetInt32(14, 17); // 14..16
        var chars = new char[len];
        // Guarantee one from each class, then fill, then shuffle (crypto RNG throughout).
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

    private static (string question, string answer) RandomSecurity(LocaleData d)
    {
        var q = Random.Shared.Next(SecurityQuestions.Length);
        var answer = q switch
        {
            0 => Pick(d.Surnames),       // mother's maiden name
            1 => Pick(Pets),             // first pet
            2 => Pick(d.Schools),        // first school
            3 => Pick(d.Cities).City,    // town born
            4 => Pick(d.MaleFirst),      // best friend
            5 => Pick(Cars),             // first car
            6 => Pick(d.FemaleFirst),    // favourite teacher
            _ => Pick(Foods)             // favourite food
        };
        return (SecurityQuestions[q], answer);
    }

    // ---- Data pools ----------------------------------------------------------------------------

    private sealed class LocaleData
    {
        public required string[] MaleFirst { get; init; }
        public required string[] FemaleFirst { get; init; }
        public required string[] Surnames { get; init; }
        public required string[] StreetNames { get; init; }
        public required string[] StreetTypes { get; init; }
        public required (string City, string Region)[] Cities { get; init; }
        public required string[] Schools { get; init; }
    }

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
        "Hyundai i30", "Mazda 3", "Chevrolet Malibu", "Jeep Wrangler", "Subaru Impreza"
    ];

    private static readonly string[] Foods =
    [
        "Pizza", "Sushi", "Lasagne", "Curry", "Tacos", "Burgers", "Pasta", "Roast dinner",
        "Fish and chips", "Ramen", "Pad Thai", "Steak", "Risotto", "Paella", "Burrito"
    ];

    private static readonly LocaleData Uk = new()
    {
        MaleFirst =
        [
            "James", "Oliver", "Harry", "Jack", "George", "Noah", "Charlie", "Jacob", "Thomas", "Oscar",
            "William", "Henry", "Leo", "Alfie", "Joshua", "Freddie", "Archie", "Logan", "Theo", "Arthur",
            "Mason", "Daniel", "Edward", "Samuel", "Joseph", "Max", "Lucas", "Ethan", "Alexander", "Benjamin",
            "Sebastian", "Harrison", "Dylan", "Callum", "Liam", "Nathan", "Ryan", "Aaron", "Connor", "Jamie"
        ],
        FemaleFirst =
        [
            "Olivia", "Amelia", "Isla", "Ava", "Emily", "Sophia", "Grace", "Mia", "Poppy", "Ella",
            "Lily", "Charlotte", "Evie", "Sophie", "Isabella", "Freya", "Daisy", "Phoebe", "Florence", "Alice",
            "Jessica", "Ruby", "Chloe", "Holly", "Lucy", "Emma", "Hannah", "Megan", "Eleanor", "Maisie",
            "Imogen", "Bethany", "Abigail", "Molly", "Scarlett", "Rosie", "Niamh", "Erin", "Katie", "Zoe"
        ],
        Surnames =
        [
            "Smith", "Jones", "Williams", "Taylor", "Brown", "Davies", "Evans", "Wilson", "Thomas", "Roberts",
            "Johnson", "Lewis", "Walker", "Robinson", "Wood", "Thompson", "White", "Watson", "Jackson", "Wright",
            "Green", "Harris", "Cooper", "King", "Lee", "Martin", "Clarke", "James", "Morgan", "Hughes",
            "Edwards", "Hill", "Moore", "Clark", "Harrison", "Scott", "Young", "Morris", "Hall", "Ward",
            "Turner", "Carter", "Phillips", "Mitchell", "Patel", "Adams", "Campbell", "Anderson", "Allen", "Bell"
        ],
        StreetNames =
        [
            "High", "Station", "Church", "Victoria", "Main", "Park", "Mill", "Queens", "Kings", "New",
            "Manor", "School", "North", "Green", "Springfield", "George", "Albert", "Bridge", "Grange", "Windsor",
            "York", "Chapel", "Meadow", "Oak", "Elm", "Highfield", "Woodland", "Riverside", "Castle", "Abbey",
            "Priory", "Beech", "Hawthorn", "Willow", "Cedar", "Birch", "Maple", "West", "South", "Orchard"
        ],
        StreetTypes =
        [
            "Road", "Street", "Lane", "Avenue", "Close", "Drive", "Way", "Gardens", "Grove", "Crescent",
            "Place", "Court", "Terrace", "Walk"
        ],
        Cities =
        [
            ("London", "Greater London"), ("Manchester", "Greater Manchester"), ("Birmingham", "West Midlands"),
            ("Leeds", "West Yorkshire"), ("Liverpool", "Merseyside"), ("Sheffield", "South Yorkshire"),
            ("Bristol", "Bristol"), ("Newcastle", "Tyne and Wear"), ("Nottingham", "Nottinghamshire"),
            ("Leicester", "Leicestershire"), ("Brighton", "East Sussex"), ("Southampton", "Hampshire"),
            ("Reading", "Berkshire"), ("Oxford", "Oxfordshire"), ("Cambridge", "Cambridgeshire"),
            ("York", "North Yorkshire"), ("Norwich", "Norfolk"), ("Plymouth", "Devon"), ("Exeter", "Devon"),
            ("Bath", "Somerset"), ("Cardiff", "South Glamorgan"), ("Swansea", "West Glamorgan"),
            ("Edinburgh", "Midlothian"), ("Glasgow", "Lanarkshire"), ("Aberdeen", "Aberdeenshire"),
            ("Coventry", "West Midlands"), ("Hull", "East Yorkshire"), ("Derby", "Derbyshire"),
            ("Preston", "Lancashire"), ("Ipswich", "Suffolk")
        ],
        Schools =
        [
            "St Mary's Primary School", "Greenfield High School", "Oakwood Academy", "Victoria Primary School",
            "Park View School", "Riverside Comprehensive", "Hillcrest Academy", "St John's CofE School",
            "Meadow Primary School", "Kingsway High School", "Brookfield School", "Highfield Academy",
            "The Grove School", "Abbey Park School", "Woodlands Primary School"
        ]
    };

    private static readonly LocaleData Us = new()
    {
        MaleFirst =
        [
            "Liam", "Noah", "William", "James", "Oliver", "Benjamin", "Elijah", "Lucas", "Mason", "Logan",
            "Alexander", "Ethan", "Jacob", "Michael", "Daniel", "Henry", "Jackson", "Sebastian", "Aiden", "Matthew",
            "Samuel", "David", "Joseph", "Carter", "Owen", "Wyatt", "John", "Jack", "Luke", "Jayden",
            "Dylan", "Grayson", "Levi", "Isaac", "Gabriel", "Julian", "Anthony", "Christopher", "Joshua", "Andrew"
        ],
        FemaleFirst =
        [
            "Emma", "Olivia", "Ava", "Isabella", "Sophia", "Mia", "Charlotte", "Amelia", "Harper", "Evelyn",
            "Abigail", "Emily", "Elizabeth", "Avery", "Sofia", "Ella", "Madison", "Scarlett", "Victoria", "Grace",
            "Chloe", "Camila", "Penelope", "Riley", "Layla", "Lillian", "Nora", "Zoey", "Hannah", "Lily",
            "Ellie", "Addison", "Natalie", "Brooklyn", "Hailey", "Savannah", "Aria", "Aubrey", "Stella", "Allison"
        ],
        Surnames =
        [
            "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez",
            "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin",
            "Lee", "Perez", "Thompson", "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson",
            "Walker", "Young", "Allen", "King", "Wright", "Scott", "Torres", "Nguyen", "Hill", "Flores",
            "Green", "Adams", "Nelson", "Baker", "Hall", "Rivera", "Campbell", "Mitchell", "Carter", "Roberts"
        ],
        StreetNames =
        [
            "Main", "Oak", "Pine", "Maple", "Cedar", "Elm", "Washington", "Lake", "Hill", "Park",
            "Walnut", "Spring", "North", "Highland", "Sunset", "Lincoln", "Church", "River", "Meadow", "Forest",
            "Madison", "Jefferson", "Franklin", "Willow", "Birch", "Chestnut", "Ridge", "Valley", "Center", "Jackson",
            "Adams", "Liberty", "Union", "College", "Prospect", "Cherry", "Dogwood", "Magnolia", "Aspen", "Cypress"
        ],
        StreetTypes =
        [
            "St", "Ave", "Dr", "Blvd", "Ln", "Rd", "Ct", "Pl", "Way", "Ter", "Cir", "Trail"
        ],
        Cities =
        [
            ("New York", "NY"), ("Los Angeles", "CA"), ("Chicago", "IL"), ("Houston", "TX"), ("Phoenix", "AZ"),
            ("Philadelphia", "PA"), ("San Antonio", "TX"), ("San Diego", "CA"), ("Dallas", "TX"), ("Austin", "TX"),
            ("San Jose", "CA"), ("Jacksonville", "FL"), ("Columbus", "OH"), ("Charlotte", "NC"), ("Indianapolis", "IN"),
            ("Seattle", "WA"), ("Denver", "CO"), ("Boston", "MA"), ("Nashville", "TN"), ("Portland", "OR"),
            ("Las Vegas", "NV"), ("Detroit", "MI"), ("Memphis", "TN"), ("Atlanta", "GA"), ("Miami", "FL"),
            ("Minneapolis", "MN"), ("Kansas City", "MO"), ("Raleigh", "NC"), ("Omaha", "NE"), ("Tampa", "FL")
        ],
        Schools =
        [
            "Lincoln High School", "Washington Elementary", "Riverside Middle School", "Jefferson High School",
            "Oakdale Elementary", "Maplewood High School", "Roosevelt Middle School", "Kennedy High School",
            "Westfield Elementary", "Hillside High School", "Central High School", "Eastwood Elementary",
            "Pine Valley School", "Sunset Ridge Academy", "Lakeview High School"
        ]
    };
}
