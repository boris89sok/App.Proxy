namespace App.MinimalApi.Model.Models
{
    public class DTOCountryResponse
    {
        [JsonPropertyName("name")]
        public DTOCountry? Name { get; set; }

        [JsonPropertyName("capital")]
        public List<string>? Capital { get; set; }

        [JsonPropertyName("currencies")]
        public Dictionary<string, DTOCurrencyDetail>? Currencies { get; set; }
    }
}