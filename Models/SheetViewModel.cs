namespace CandyCottage.CandyBot.Models
{
    public class SheetViewModel
    {
        public string FileId { get; set; } = "";
        public string CurrentTitle { get; set; } = "";
        public List<string> SheetTitles { get; set; } = [];
        public List<List<string>> CurrentGrid { get; set; } = [];
        public Dictionary<string, string> Messages { get; set; } = [];
        public bool HasData => SheetTitles.Count > 0 && CurrentGrid.Count > 0;
    }
}
