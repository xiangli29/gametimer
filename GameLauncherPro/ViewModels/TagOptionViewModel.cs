using GameLauncherPro;

namespace GameLauncherPro.ViewModels
{
    public sealed class TagOptionViewModel : TagDisplayViewModel
    {
        private bool _isSelected;

        public TagOptionViewModel(TagDefinition definition, bool isSelected)
            : base(definition)
        {
            _isSelected = isSelected;
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                Raise(nameof(IsSelected));
            }
        }
    }
}
