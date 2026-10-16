using System.ComponentModel;

namespace VisualTTY;

public class LinuxPackageInfo : INotifyPropertyChanged
{
    public enum PackageState
    {
        Installed,
        Upgradeable,
        Available
    }

    public enum PackageAction
    {
        None,
        Install,
        Remove
    }

    private bool? _IsChecked;

    public string ID { get; set; }

    public string UserFriendlyName { get; set; }

    public string Version { get; set; }

    public string Name => UserFriendlyName ?? ID;

    public string RawState { get; set; }

    public PackageState State { get; set; }

    public string DescriptiveState
    {
        get
        {
            if (RawState != null)
            {
                return RawState;
            }
            return State switch
            {
                PackageState.Available => "not installed",
                PackageState.Installed => "installed",
                PackageState.Upgradeable => "update available",
                _ => State.ToString(),
            };
        }
    }

    public bool? IsChecked
    {
        get
        {
            return _IsChecked;
        }
        set
        {
            _IsChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsChecked"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Action"));
        }
    }

    public bool IsThreeState => State == PackageState.Upgradeable;

    public PackageAction Action
    {
        get
        {
            switch (State)
            {
                case PackageState.Available:
                    if (IsChecked != true)
                    {
                        return PackageAction.None;
                    }
                    return PackageAction.Install;
                case PackageState.Installed:
                    if (IsChecked != false)
                    {
                        return PackageAction.None;
                    }
                    return PackageAction.Remove;
                case PackageState.Upgradeable:
                    if (IsChecked == true)
                    {
                        return PackageAction.Install;
                    }
                    if (IsChecked == false)
                    {
                        return PackageAction.Remove;
                    }
                    return PackageAction.None;
                default:
                    return PackageAction.None;
            }
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public override string ToString()
    {
        return Name;
    }

    public void ToggleSelection()
    {
        if (IsThreeState)
        {
            bool? isChecked = IsChecked;
            if (isChecked.HasValue)
            {
                if (isChecked == true)
                {
                    IsChecked = false;
                }
                else
                {
                    IsChecked = null;
                }
            }
            else
            {
                IsChecked = true;
            }
        }
        else
        {
            IsChecked = !IsChecked;
        }
    }
}
