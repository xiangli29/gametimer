namespace GameLauncherPro
{
    public class PlaySession
    {
        public string game_id { get; set; } = "";
        public string started_at { get; set; } = "";
        public string ended_at { get; set; } = "";
        public int duration_seconds { get; set; }
    }
}
