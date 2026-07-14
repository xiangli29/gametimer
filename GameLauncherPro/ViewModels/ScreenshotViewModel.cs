using System.ComponentModel;
using System.Windows.Media;

namespace GameLauncherPro.ViewModels
{
    public sealed class ScreenshotViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public ScreenshotViewModel(string imagePath)
        {
            ImagePath = imagePath;
        }

        public string ImagePath { get; }

        private ImageSource? _image;
        public ImageSource? Image
        {
            get => _image;
            set
            {
                _image = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }
    }
}
