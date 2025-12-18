namespace NewAxis.Services
{
    /// <summary>
    /// Represents a game setting override from SettingsPlus or SettingsUltra
    /// </summary>
    public class GameSettingOverride
    {
        public string? GameSettingId { get; set; }
        public string? Value { get; set; }
        public int OptionType { get; set; }
    }
}
