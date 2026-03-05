namespace App.MinimalApi.Model.Models
{
    public class DTOCurrencyDetail
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("symbol")]
        public string? Symbol { get; set; }
    }
}
