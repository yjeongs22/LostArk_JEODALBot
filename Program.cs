using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

// 단일 캐릭터 정보
public class UserRaidInfo
{
    public string CharacterName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ItemLevel { get; set; } = "";
    public bool IsSupport { get; set; }
    public string RoleIcon { get; set; } = "";
    //푸시제발돼라

    // 직업군 자동 분류 (서포터 판독기)
    public bool IsSupport => ClassName == "바드" || ClassName == "홀리나이트" || ClassName == "도화가";
    public string RoleIcon => IsSupport ? "🛡️서폿" : "⚔️딜러";
}

// 다중 캐릭터를 관리할 유저 프로필
public class UserProfile
{
    public string ActiveCharacter { get; set; } = ""; // 현재 레이드에 참가할 대표 캐릭터
    public Dictionary<string, UserRaidInfo> Characters { get; set; } = new Dictionary<string, UserRaidInfo>(); // 내 캐릭터 목록
}

class Program
{
    private DiscordSocketClient _client = null!;
    private readonly HttpClient _httpClient = new HttpClient();
    private IMongoCollection<BsonDocument> _collection = null!;

    // 데이터베이스 구조 변경 (유저ID -> 유저프로필)
    private Dictionary<ulong, UserProfile> _userDatabase = new Dictionary<ulong, UserProfile>();
    private readonly string _dbFilePath = "user_data.json";

    // ★ 로스트아크 API 키
    private readonly string _lostArkApiKey = Environment.GetEnvironmentVariable("LOSTARK_API_KEY") ?? "";


    static void Main(string[] args) => new Program().MainAsync().GetAwaiter().GetResult();

    public async Task MainAsync()
    {
        LoadDatabase();

        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
        };
        _client = new DiscordSocketClient(config);

        _client.Log += Log;
        _client.Ready += Client_Ready;
        _client.InteractionCreated += Client_InteractionCreated;
        _client.AutocompleteExecuted += Client_AutocompleteExecuted;

        // ★ 디스코드 봇 토큰
        string token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ?? "";

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        await Task.Delay(-1);
    }

    private Task Log(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }

    private async Task Client_Ready()
    {
        // 1. 레이드 명령어 (보스 선택에 따라 난이도 연동)
        var raidCommand = new SlashCommandBuilder()
            .WithName("레이드")
            .WithDescription("새로운 레이드 일정을 등록합니다.")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("보스")
                .WithDescription("레이드 보스 선택")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .AddChoice("1막: 대지를 부수는 업화의 궤적", "1막: 대지를 부수는 업화의 궤적")
                .AddChoice("2막: 부유하는 악몽의 진혼곡", "2막: 부유하는 악몽의 진혼곡")
                .AddChoice("3막: 칠흑, 폭풍의 밤", "3막: 칠흑, 폭풍의 밤")
                .AddChoice("4막: 파멸의 성채", "4막: 파멸의 성채")
                .AddChoice("종막: 최후의 날", "종막: 최후의 날")
                .AddChoice("고통의 마녀 세르카", "고통의 마녀 세르카")
                .AddChoice("지평의 성당", "지평의 성당"))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("난이도")
                .WithDescription("난이도 선택 (보스에 따라 다릅니다)")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .WithAutocomplete(true))
            // ▼ 바로 이 부분이 문제의 원인이었습니다! 아래처럼 묶어주면 해결! ▼
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("시간")
                .WithDescription("출발 시간 (예: 8시)")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true));

        // 2. 연동 명령어
        var linkCommand = new SlashCommandBuilder()
            .WithName("연동")
            .WithDescription("내 캐릭터를 목록에 추가합니다.")
            .AddOption("캐릭터명", ApplicationCommandOptionType.String, "조회할 캐릭터 이름", isRequired: true);

        // 3. 내 정보 명령어
        var infoCommand = new SlashCommandBuilder()
            .WithName("내정보")
            .WithDescription("등록된 내 캐릭터 목록과 대표 캐릭터를 확인합니다.");

        // 4. 대표 캐릭터 설정 명령어
        var setMainCommand = new SlashCommandBuilder()
            .WithName("대표설정")
            .WithDescription("레이드에 참가할 대표 캐릭터를 변경합니다.")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("캐릭터명")
                .WithDescription("대표로 설정할 캐릭터를 선택하세요.")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .WithAutocomplete(true));

        try
        {
            ulong[] guildIds = { 1378050723476144268, 1284878074692763668 };

            foreach (ulong id in guildIds)
            {
                var guild = _client.GetGuild(id);

                if (guild != null)
                {
                    await guild.CreateApplicationCommandAsync(raidCommand.Build());
                    await guild.CreateApplicationCommandAsync(linkCommand.Build());
                    await guild.CreateApplicationCommandAsync(infoCommand.Build());
                    await guild.CreateApplicationCommandAsync(setMainCommand.Build());
                    Console.WriteLine($"서버({id})에 모든 명령어 즉시 등록 완료!");
                }
                else
                {
                    Console.WriteLine($"서버({id})를 찾을 수 없습니다. 해당 서버에 봇이 초대되어 있는지 확인하세요.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"명령어 등록 에러: {ex.Message}");
        }
    }

    private async Task Client_InteractionCreated(SocketInteraction interaction)
    {
        if (interaction is SocketSlashCommand command)
        {
            ulong userId = command.User.Id;

            if (command.Data.Name == "레이드")
            {
                string boss = command.Data.Options.First(x => x.Name == "보스").Value.ToString()!;
                string difficulty = command.Data.Options.First(x => x.Name == "난이도").Value.ToString()!;
                string time = command.Data.Options.First(x => x.Name == "시간").Value.ToString()!;

                var embed = new EmbedBuilder()
                    .WithTitle($"⚔️ {boss} [{difficulty}] 레이드 모집")
                    .WithDescription($"**출발 시간:** {time}\n\n**참가자 명단:**\n(아직 참가자가 없습니다.)")
                    .WithColor(Color.Orange)
                    .Build();

                var builder = new ComponentBuilder()
                    .WithButton("참가하기", "btn_join", ButtonStyle.Success)
                    .WithButton("취소하기", "btn_cancel", ButtonStyle.Danger);

                await command.RespondAsync(embed: embed, components: builder.Build());
            }
            else if (command.Data.Name == "연동")
            {
                await command.DeferAsync(ephemeral: true);
                string charName = command.Data.Options.First(x => x.Name == "캐릭터명").Value.ToString()!;

                var characterInfo = await FetchLostArkCharacterAsync(charName);
                if (characterInfo == null)
                {
                    await command.FollowupAsync("❌ 캐릭터를 찾을 수 없습니다.", ephemeral: true);
                    return;
                }

                if (!_userDatabase.ContainsKey(userId))
                    _userDatabase[userId] = new UserProfile();

                _userDatabase[userId].Characters[characterInfo.CharacterName] = characterInfo;
                _userDatabase[userId].ActiveCharacter = characterInfo.CharacterName;
                SaveDatabase();

                await command.FollowupAsync($"✅ **{characterInfo.CharacterName}** 캐릭터가 목록에 추가되었으며, 현재 대표 캐릭터로 자동 설정되었습니다!", ephemeral: true);
            }
            else if (command.Data.Name == "내정보")
            {
                if (!_userDatabase.ContainsKey(userId) || _userDatabase[userId].Characters.Count == 0)
                {
                    await command.RespondAsync("❌ 등록된 캐릭터가 없습니다. `/연동` 명령어로 먼저 등록해주세요.", ephemeral: true);
                    return;
                }

                var profile = _userDatabase[userId];
                string infoText = "";

                foreach (var character in profile.Characters.Values)
                {
                    string isMain = (character.CharacterName == profile.ActiveCharacter) ? " 👑 **(현재 대표)**" : "";
                    infoText += $"- {character.RoleIcon} [{character.ServerName}] **{character.CharacterName}** ({character.ClassName} / {character.ItemLevel}){isMain}\n";
                }

                var embed = new EmbedBuilder()
                    .WithTitle($"{command.User.Username}님의 로스터")
                    .WithDescription(infoText)
                    .WithColor(Color.Green)
                    .Build();

                await command.RespondAsync(embed: embed, ephemeral: true);
            }
            else if (command.Data.Name == "대표설정")
            {
                string targetName = command.Data.Options.First(x => x.Name == "캐릭터명").Value.ToString()!;

                if (targetName == "error_no_char")
                {
                    await command.RespondAsync("❌ 먼저 `/연동` 명령어로 캐릭터를 등록해주세요.", ephemeral: true);
                    return;
                }

                _userDatabase[userId].ActiveCharacter = targetName;
                SaveDatabase();
                await command.RespondAsync($"✅ 레이드 참가용 대표 캐릭터가 **{targetName}** (으)로 변경되었습니다!", ephemeral: true);
            }
        }
        else if (interaction is SocketMessageComponent component)
        {
            var originalEmbed = component.Message.Embeds.First();
            string description = originalEmbed.Description;
            ulong userId = component.User.Id;
            string userName = component.User.GlobalName ?? component.User.Username;

            var lines = description.Split('\n').ToList();

            if (component.Data.CustomId == "btn_join")
            {
                bool isAlreadyJoined = lines.Any(line => line.Contains(userName));
                if (isAlreadyJoined)
                {
                    await component.RespondAsync("이미 명단에 이름이 있습니다! 부캐로 바꾸려면 취소 후 대표캐릭터를 변경하세요.", ephemeral: true);
                    return;
                }

                lines.RemoveAll(line => line.Contains("(아직 참가자가 없습니다.)"));

                if (_userDatabase.ContainsKey(userId) && _userDatabase[userId].Characters.Count > 0)
                {
                    var profile = _userDatabase[userId];
                    var info = profile.Characters[profile.ActiveCharacter];
                    lines.Add($"- {info.RoleIcon} [{info.ClassName}/{info.ItemLevel}] {info.CharacterName} ({userName})");
                }
                else
                {
                    lines.Add($"- ⚠️미연동유저 ({userName})");
                }
            }
            else if (component.Data.CustomId == "btn_cancel")
            {
                bool hasJoined = lines.Any(line => line.Contains(userName));
                if (!hasJoined)
                {
                    await component.RespondAsync("참가 명단에 없어서 취소할 수 없습니다!", ephemeral: true);
                    return;
                }

                lines.RemoveAll(line => line.Contains(userName));

                if (lines.Last().Contains("**참가자 명단:**"))
                {
                    lines.Add("(아직 참가자가 없습니다.)");
                }
            }

            string newDescription = string.Join('\n', lines);
            var newEmbed = new EmbedBuilder().WithTitle(originalEmbed.Title).WithDescription(newDescription).WithColor(Color.Orange).Build();
            await component.UpdateAsync(x => x.Embed = newEmbed);
        }
    }

    private async Task Client_AutocompleteExecuted(SocketAutocompleteInteraction interaction)
    {
        if (interaction.Data.CommandName == "대표설정")
        {
            ulong userId = interaction.User.Id;
            string userInput = (interaction.Data.Current.Value ?? "").ToString();

            var results = new List<AutocompleteResult>();

            if (_userDatabase.ContainsKey(userId) && _userDatabase[userId].Characters.Count > 0)
            {
                var myCharacters = _userDatabase[userId].Characters.Values;

                foreach (var character in myCharacters)
                {
                    string displayName = $"[{character.ClassName}/{character.ItemLevel}] {character.CharacterName}";
                    results.Add(new AutocompleteResult(displayName, character.CharacterName));
                }
            }
            else
            {
                results.Add(new AutocompleteResult("❌ 등록된 캐릭터가 없습니다. /연동 먼저 해주세요.", "error_no_char"));
            }

            await interaction.RespondAsync(results.Take(25));
        }

        if (interaction.Data.CommandName == "레이드" && interaction.Data.Current.Name == "난이도")
        {
            var bossOption = interaction.Data.Options.FirstOrDefault(x => x.Name == "보스");
            string selectedBoss = bossOption?.Value?.ToString() ?? "";

            var results = new List<AutocompleteResult>();

            switch (selectedBoss)
            {
                case "1막: 대지를 부수는 업화의 궤적":
                    results.Add(new AutocompleteResult("노말", "노말"));
                    results.Add(new AutocompleteResult("하드", "하드"));
                    break;
                case "2막: 부유하는 악몽의 진혼곡":
                    results.Add(new AutocompleteResult("노말", "노말"));
                    results.Add(new AutocompleteResult("하드", "하드"));
                    results.Add(new AutocompleteResult("익스트림 노말", "익스트림 노말"));
                    results.Add(new AutocompleteResult("익스트림 하드", "익스트림 하드"));
                    results.Add(new AutocompleteResult("익스트림 나이트메어", "익스트림 나이트메어"));
                    break;
                case "3막: 칠흑, 폭풍의 밤":
                    results.Add(new AutocompleteResult("노말", "노말"));
                    results.Add(new AutocompleteResult("하드", "하드"));
                    break;
                case "4막: 파멸의 성채":
                    results.Add(new AutocompleteResult("노말", "노말"));
                    results.Add(new AutocompleteResult("하드", "하드"));
                    break;
                case "종막: 최후의 날":
                    results.Add(new AutocompleteResult("노말", "노말"));
                    results.Add(new AutocompleteResult("하드", "하드"));
                    break;
                case "고통의 마녀 세르카":
                    results.Add(new AutocompleteResult("노말", "노말"));
                    results.Add(new AutocompleteResult("하드", "하드"));
                    results.Add(new AutocompleteResult("나이트메어", "나이트메어"));
                    break;
                case "지평의 성당":
                    results.Add(new AutocompleteResult("1단계", "1단계"));
                    results.Add(new AutocompleteResult("2단계", "2단계"));
                    results.Add(new AutocompleteResult("3단계", "3단계"));
                    break;
                default:
                    results.Add(new AutocompleteResult("⚠️ 앞칸에서 보스를 먼저 선택해주세요!", "미선택"));
                    break;
            }

            await interaction.RespondAsync(results.Take(25));
        }
    }

    private async Task<UserRaidInfo?> FetchLostArkCharacterAsync(string characterName)
    {
        UserRaidInfo? resultInfo = null;
        try
        {
            string url = $"https://developer-lostark.game.onstove.com/armories/characters/{Uri.EscapeDataString(characterName)}/profiles";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _lostArkApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                string jsonResult = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(jsonResult) || jsonResult == "null") return null;

                using JsonDocument doc = JsonDocument.Parse(jsonResult);
                JsonElement root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    resultInfo = new UserRaidInfo();
                    if (root.TryGetProperty("CharacterName", out var nameProp)) resultInfo.CharacterName = nameProp.GetString() ?? "";
                    if (root.TryGetProperty("ServerName", out var serverProp)) resultInfo.ServerName = serverProp.GetString() ?? "";
                    if (root.TryGetProperty("CharacterClassName", out var classProp)) resultInfo.ClassName = classProp.GetString() ?? "";
                    if (root.TryGetProperty("ItemAvgLevel", out var itemProp)) resultInfo.ItemLevel = itemProp.GetString() ?? "";

                    if (string.IsNullOrEmpty(resultInfo.CharacterName)) return null;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"API 오류: {ex.Message}");
        }
        return resultInfo;
    }

    private void SaveDatabase()
    {
        try
        {
            foreach (var entry in _userDatabase)
            {
                string userIdStr = entry.Key.ToString();
                var profileBson = entry.Value.ToBsonDocument();

                // 기존 데이터가 있으면 업데이트(Upsert), 없으면 생성
                var filter = Builders<BsonDocument>.Filter.Eq("userId", userIdStr);
                var update = Builders<BsonDocument>.Update
                    .Set("userId", userIdStr)
                    .Set("profile", profileBson);

                _collection.UpdateOne(filter, update, new UpdateOptions { IsUpsert = true });
            }
            Console.WriteLine("✅ DB에 데이터를 저장했습니다.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ DB 저장 실패: {ex.Message}");
        }
    }

    private void LoadDatabase()
    {
        try
        {
            // 괄호 다 지우고 비밀번호까지 넣은 실제 주소를 여기에 넣어!
            var connectionString = "mongodb+srv://yjeongs22:yjeongs22ppppp@cluster0.3ngir1y.mongodb.net/";

            var client = new MongoClient(connectionString);
            var database = client.GetDatabase("RaidBot");
            _collection = database.GetCollection<BsonDocument>("Users");

            var filter = new BsonDocument();
            var documents = _collection.Find(filter).ToList();

            _userDatabase = new Dictionary<ulong, UserProfile>();
            foreach (var doc in documents)
            {
                // DB에서 가져올 때 필드명이 "userId"와 "profile"인지 꼭 확인해!
                ulong userId = ulong.Parse(doc["userId"].AsString);
                var profile = BsonSerializer.Deserialize<UserProfile>(doc["profile"].AsBsonDocument);
                _userDatabase[userId] = profile;
            }
            Console.WriteLine("✅ DB에서 데이터를 성공적으로 불러왔습니다.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ DB 로드 실패: {ex.Message}");
        }
    }
}
