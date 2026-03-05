namespace App.MinimalApi.Model.Models
{
    public class DTOCountry
    {
        [JsonPropertyName("common")]
        public string? Common { get; set; }

        [JsonPropertyName("official")]
        public string? Official { get; set; }
    }
}
