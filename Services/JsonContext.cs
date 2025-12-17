using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NewAxis.Services
{
    [JsonSerializable(typeof(GameIndex))]
    [JsonSerializable(typeof(UpdateInfo))]
    [JsonSerializable(typeof(GameIndexEntry))]
    [JsonSerializable(typeof(ImageUrls))]
    [JsonSerializable(typeof(FileUrls))]
    [JsonSerializable(typeof(List<GameIndexEntry>))]
    [JsonSerializable(typeof(MigotoInstructions))]
    [JsonSerializable(typeof(ConfigInstructions))]
    [JsonSerializable(typeof(FileInstruction))]
    [JsonSerializable(typeof(List<FileInstruction>))]
    [JsonSerializable(typeof(List<Root>))]
    [JsonSerializable(typeof(Root))]
    [JsonSerializable(typeof(Child))]
    [JsonSerializable(typeof(AvailableSettingValue))]
    [JsonSerializable(typeof(ConfigFilePath))]
    [JsonSerializable(typeof(List<Child>))]
    [JsonSerializable(typeof(List<AvailableSettingValue>))]
    [JsonSerializable(typeof(List<ConfigFilePath>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    public partial class AppJsonContext : JsonSerializerContext
    {
    }
}
