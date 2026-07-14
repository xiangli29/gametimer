using System.Collections.Generic;

namespace GameLauncherPro
{
    public class GameData
    {
        public int total_seconds { get; set; } = 0;
        public string last_play { get; set; } = "";
        public List<string> exe_paths { get; set; } = new();
        public string cover_path { get; set; } = "";
        public string cover_back_path { get; set; } = "";
        public List<string> screenshot_paths { get; set; } = new();
        // 当前显示面："front" 或 "back"
        public string current_side { get; set; } = "front";
        // 用户评分：0-10
        public int score { get; set; } = 0;
        public string launch_exe { get; set; } = "";
    }
}
