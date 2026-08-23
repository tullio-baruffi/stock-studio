namespace StockStudio.Api.Services;

/// <summary>
/// A subject family that can legally and practically become stock artwork.
/// Every theme is sellable by construction: generic concepts only, no protected names.
/// </summary>
/// <param name="Keys">Generic nouns (IT + EN). A phrase matching these IS a generic subject.</param>
/// <param name="Brands">Proper nouns that reveal the theme but are themselves protected names.</param>
/// <param name="Subjects">Concrete things to draw for this theme.</param>
/// <param name="WikiArticle">English Wikipedia article used to measure real public interest.</param>
public sealed record StockTheme(
    string Name,
    string Category,
    string WikiArticle,
    string[] Keys,
    string[] Brands,
    string[] Subjects);

/// <summary>
/// The catalogue the whole opportunity engine works from. Analysis starts here — from subjects that
/// are sellable by definition — rather than from whatever happens to be trending, which is mostly
/// people, brands and news that can never become stock artwork.
/// </summary>
public static class StockThemeCatalog
{
    public static readonly IReadOnlyList<StockTheme> All = new StockTheme[]
    {
        new("baseball", "Sport", "Baseball",
            new[]{"baseball","batter","home run","softball"},
            new[]{"mlb","world series","orioles","yankees","dodgers"},
            new[]{"batter swinging a bat","pitcher throwing","baseball glove and ball","baseball cap","stadium crowd"}),
        new("calcio", "Sport", "Association_football",
            new[]{"soccer","calcio","calciatore","football club","pallone","fútbol","futbol","goalkeeper","portiere"},
            new[]{"premier league","fifa","uefa","world cup","serie a","la liga","champions league"},
            new[]{"player kicking a ball","goalkeeper diving","soccer ball","trophy cup","stadium with flags"}),
        new("football americano", "Sport", "American_football",
            new[]{"quarterback","touchdown","american football"},
            new[]{"nfl","super bowl"},
            new[]{"quarterback throwing","football helmet","player running with ball","goal post"}),
        new("basket", "Sport", "Basketball",
            new[]{"basketball","basket","pallacanestro","slam dunk","canestro"},
            new[]{"nba"},
            new[]{"player dunking","basketball and hoop","dribbling silhouette"}),
        new("tennis", "Sport", "Tennis",
            new[]{"tennis","racchetta","racket"},
            new[]{"wimbledon","roland garros","atp","wta"},
            new[]{"player serving","racket and ball","tennis court net"}),
        new("golf", "Sport", "Golf",
            new[]{"golf","golfer","golfista"},
            new[]{"lpga","pga","ryder cup","masters tournament"},
            new[]{"golfer mid-swing","golf ball on a tee","flag on a putting green","golf cart"}),
        new("olimpiadi", "Sport", "Olympic_Games",
            new[]{"olympics","olimpiadi","olympic games","olimpico","paralympics","medal","medaglia"},
            Array.Empty<string>(),
            new[]{"athlete with a torch","podium with medals","laurel wreath","runner crossing the finish line","gymnast in the air"}),
        new("motori", "Sport", "Auto_racing",
            new[]{"racing car","auto da corsa","motorsport","motociclismo","race track"},
            new[]{"formula 1","nascar","motogp","grand prix","le mans"},
            new[]{"race car side view","checkered flag","racing helmet","motorcycle leaning into a curve"}),
        new("running e fitness", "Sport", "Running",
            new[]{"marathon","maratona","running","corsa","fitness","workout","palestra","gym","crossfit","triathlon","jogging","allenamento"},
            Array.Empty<string>(),
            new[]{"runner mid-stride","dumbbell","athlete stretching","finish line ribbon","skipping rope"}),
        new("yoga e benessere", "Benessere", "Yoga",
            new[]{"yoga","meditation","meditazione","wellness","benessere","mindfulness","pilates","relax"},
            Array.Empty<string>(),
            new[]{"lotus meditation pose","warrior yoga pose","zen stones stack","incense and candle"}),
        new("ciclismo", "Sport", "Cycling",
            new[]{"cycling","ciclismo","bicycle","bicicletta","bici","cyclist","ciclista"},
            new[]{"tour de france","giro d'italia","vuelta"},
            new[]{"cyclist racing","bicycle side view","mountain biker jumping"}),
        new("inverno e neve", "Stagione", "Skiing",
            new[]{"skiing","sci","sciatore","snowboard","neve","snowfall","ice skating","pattinaggio","sledding","slitta"},
            new[]{"winter olympics"},
            new[]{"skier downhill","snowboarder jumping","snowflake ornament","ice skater spinning"}),
        new("mare e surf", "Stagione", "Surfing",
            new[]{"surfing","surfer","surf","diving","subacquea","sailing","vela","kayak","spiaggia","beach","onda","wave"},
            Array.Empty<string>(),
            new[]{"surfer riding a wave","sailboat on the horizon","diver silhouette","palm tree and hammock"}),
        new("astronomia", "Scienza", "Astronomy",
            new[]{"eclipse","eclissi","meteor","meteora","comet","cometa","planet","pianeta","spacecraft","rocket","razzo","satellite","aurora","astronomy","astronomia","asteroid","galaxy","galassia","telescope","telescopio","astronaut","astronauta","luna"},
            new[]{"nasa","spacex"},
            new[]{"solar eclipse with corona","rocket launching with smoke","astronaut floating","planet with rings","telescope under stars","crescent moon and stars"}),
        new("meteo", "Natura", "Weather",
            new[]{"hurricane","uragano","thunderstorm","temporale","tornado","typhoon","blizzard","heat wave","monsoon","cyclone","rainbow","arcobaleno","pioggia","fulmine"},
            Array.Empty<string>(),
            new[]{"swirling storm spiral","lightning bolt over clouds","umbrella in the rain","sun with rays","wind swirl"}),
        new("halloween", "Festività", "Halloween",
            new[]{"halloween","spooky","haunted","trick or treat","zucca intagliata","strega","witch","fantasma","ghost","pipistrello"},
            Array.Empty<string>(),
            new[]{"carved pumpkin","witch flying on a broom","bats over a full moon","haunted house","black cat arching"}),
        new("natale", "Festività", "Christmas",
            new[]{"christmas","natale","natalizio","santa claus","babbo natale","xmas","advent","avvento","presepe","nativity","albero di natale","renna","reindeer"},
            Array.Empty<string>(),
            new[]{"christmas tree with star","santa sleigh with reindeer","gift box with bow","snowman","hanging ornaments"}),
        new("capodanno", "Festività", "New_Year's_Eve",
            new[]{"new year","capodanno","fireworks","fuochi d'artificio","new year's eve","brindisi"},
            Array.Empty<string>(),
            new[]{"fireworks burst","champagne glasses toasting","confetti explosion","clock striking midnight"}),
        new("pasqua", "Festività", "Easter",
            new[]{"easter","pasqua","passover","coniglietto","uovo di pasqua"},
            Array.Empty<string>(),
            new[]{"bunny with basket","decorated egg","spring chick hatching"}),
        new("san valentino", "Festività", "Valentine's_Day",
            new[]{"valentine","san valentino","cuore","innamorati","cupido","cupid"},
            Array.Empty<string>(),
            new[]{"heart shape","couple holding hands","cupid with bow","rose bouquet"}),
        new("ringraziamento", "Festività", "Thanksgiving",
            new[]{"thanksgiving","ringraziamento","tacchino"},
            Array.Empty<string>(),
            new[]{"turkey","cornucopia with harvest","pumpkin pie","family dinner table"}),
        new("festa nazionale", "Festività", "Independence_Day_(United_States)",
            new[]{"independence day","national day","festa nazionale","parade","parata","bandiera","flag"},
            new[]{"fourth of july","bastille day"},
            new[]{"waving flag","fireworks over a skyline","parade silhouette"}),
        new("animali", "Natura", "Animal",
            new[]{"dog","cane","cat","gatto","gattino","puppy","cucciolo","panda","lion","leone","tiger","tigre","wolf","lupo","bear","orso","elephant","elefante","horse","cavallo","bird","uccello","eagle","aquila","owl","gufo","shark","squalo","whale","balena","dolphin","delfino","butterfly","farfalla","dinosaur","dinosauro","fox","volpe","deer","cervo","penguin","pinguino","giraffe","giraffa","coniglio","rabbit"},
            Array.Empty<string>(),
            new[]{"animal profile silhouette","animal running","animal family group","paw prints trail"}),
        new("natura", "Natura", "Nature",
            new[]{"forest","foresta","bosco","mountain","montagna","tree","albero","flower","fiore","garden","giardino","waterfall","cascata","desert","deserto","jungle","giungla","national park","wildlife","botanical","paesaggio","tramonto","sunset"},
            Array.Empty<string>(),
            new[]{"mountain range layers","lone tree on a hill","ocean wave curl","palm trees at sunset","flower bouquet"}),
        new("musica", "Cultura", "Music",
            new[]{"music","musica","concert","concerto","guitar","chitarra","piano","pianoforte","orchestra","jazz","violin","violino","drummer","batteria","microfono","microphone","cuffie","headphones","note musicali"},
            Array.Empty<string>(),
            new[]{"electric guitar","microphone on stand","headphones","piano keys","crowd with raised hands","music notes flowing"}),
        new("cinema e arte", "Cultura", "Film",
            new[]{"cinema","film festival","theatre","teatro","painting","pittura","museum","museo","sculpture","scultura","photography","fotografia","artwork","gallery","pennello","macchina fotografica","ciak"},
            new[]{"oscar","cannes","sundance"},
            new[]{"film clapperboard","movie camera","theatre masks","paint brush and palette","camera silhouette"}),
        new("viaggi", "Lifestyle", "Tourism",
            new[]{"travel","viaggio","tourism","turismo","airline flight","aereo","airplane","vacation","vacanza","cruise","crociera","passport","passaporto","road trip","backpacking","landmark","skyline","valigia","suitcase","mappa"},
            Array.Empty<string>(),
            new[]{"airplane taking off","suitcase with stickers","world map with pins","camper van","city skyline"}),
        new("cibo", "Lifestyle", "Food",
            new[]{"food","cibo","recipe","ricetta","coffee","caffè","pizza","burger","hamburger","wine","vino","beer","birra","chocolate","cioccolato","bakery","panetteria","barbecue","sushi","vegan","cocktail","restaurant","ristorante","cucina","dolce","cupcake"},
            Array.Empty<string>(),
            new[]{"coffee cup with steam","pizza slice","wine glass and bottle","chef hat and utensils","cupcake"}),
        new("tecnologia e AI", "Tecnologia", "Artificial_intelligence",
            new[]{"artificial intelligence","intelligenza artificiale","machine learning","robot","robotica","robotics","semiconductor","quantum computing","software","startup","cryptocurrency","criptovaluta","bitcoin","blockchain","drone","virtual reality","realtà virtuale","smartphone","computer","circuito"},
            new[]{"chatgpt","openai","nvidia"},
            new[]{"robot head profile","brain made of circuits","network of connected nodes","drone flying","VR headset","smartphone in hand"}),
        new("business e finanza", "Business", "Finance",
            new[]{"stock market","borsa","economy","economia","inflation","inflazione","mortgage","mutuo","banking","banca","investment","investimento","tariff","recession","interest rate","earnings","grafico","business","ufficio"},
            Array.Empty<string>(),
            new[]{"rising bar chart with arrow","handshake","coin stack","briefcase","house with key","office skyline"}),
        new("salute", "Salute", "Health",
            new[]{"healthcare","salute","hospital","ospedale","vaccine","vaccino","medicine","medicina","mental health","cancer awareness","nutrition","nutrizione","dentistry","dentista","stetoscopio","medico","dna"},
            Array.Empty<string>(),
            new[]{"heartbeat line","stethoscope","medical cross","pill capsule","dna helix","tooth"}),
        new("scuola", "Educazione", "School",
            new[]{"school","scuola","university","università","graduation","laurea","student","studente","education","istruzione","college","classroom","aula","library","biblioteca","libri","books","zaino"},
            Array.Empty<string>(),
            new[]{"graduation cap and diploma","stack of books","pencil and notebook","backpack","apple on books"}),
        new("ambiente", "Ambiente", "Climate_change",
            new[]{"climate change","cambiamento climatico","sustainability","sostenibilità","recycling","riciclo","renewable","rinnovabile","solar panel","pannello solare","wind turbine","pala eolica","earth day","green energy","climate summit","ecologia"},
            new[]{"cop28","cop29","cop30","cop31","cop32"},
            new[]{"wind turbine","recycling arrows","leaf in a hand","planet earth","solar panel"}),
        new("elezioni", "Attualità", "Election",
            new[]{"election","elezioni","ballot","scheda elettorale","referendum","voting","voto","electoral","urna"},
            Array.Empty<string>(),
            new[]{"ballot box with hand","checkmark on a ballot","podium with microphones"}),
        new("matrimonio", "Lifestyle", "Wedding",
            new[]{"wedding","matrimonio","bride","sposa","groom","sposo","marriage","engagement ring","fedi","bouquet"},
            Array.Empty<string>(),
            new[]{"wedding rings","bride and groom","bouquet","wedding cake"}),
        new("famiglia", "Lifestyle", "Family",
            new[]{"baby","bambino","neonato","pregnancy","gravidanza","motherhood","mamma","fatherhood","papà","parenting","toddler","famiglia","family","passeggino"},
            Array.Empty<string>(),
            new[]{"stroller","family holding hands","pregnant silhouette","teddy bear"}),
        new("lavoro", "Business", "Employment",
            new[]{"remote work","smart working","workplace","career","carriera","recruitment","productivity","produttività","coworking","freelance","scrivania","laptop","riunione"},
            Array.Empty<string>(),
            new[]{"laptop workstation","person at a desk","team meeting silhouettes","clipboard checklist"}),
        new("gaming", "Tecnologia", "Video_game",
            new[]{"gaming","esports","video game","videogioco","joystick","controller"},
            new[]{"nintendo","playstation","xbox","twitch","fortnite"},
            new[]{"game controller","arcade joystick","headset with mic","pixel heart"}),
    };

    public static StockTheme? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Turns drawable subjects into prompts ready to paste into the image generator.</summary>
    public static IReadOnlyList<string> BuildPrompts(StockTheme theme, string style, string? flavour = null, int take = 4)
    {
        string suffix = style switch
        {
            "flat" => "flat vector illustration, bold simple shapes, limited color palette, white background, no text",
            "lineart" => "clean single-weight line art, black lines on white background, no shading, no text",
            _ => "solid black silhouette on a pure white background, high contrast, no gradients, no outlines, no text, centered, clean shape suitable for vector tracing",
        };

        var season = !string.IsNullOrWhiteSpace(flavour) && flavour!.Length < 40 ? $" ({flavour} theme)" : "";
        return theme.Subjects.Take(take).Select(s => $"{s}{season}, {suffix}").ToList();
    }
}
