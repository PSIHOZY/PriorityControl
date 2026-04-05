using System;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace PriorityControl.Models
{
    [DataContract]
    public sealed class AppEntry : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _exePath = string.Empty;
        private FixedPriority _priority = FixedPriority.Normal;
        private bool _runOnStartupWithLock;
        private string _runtimeStatus = "Not running";
        private int? _processId;
        private bool _isPriorityLocked;

        [DataMember(Order = 0)]
        public string Id
        {
            get { return _id; }
            set { SetField(ref _id, value, "Id"); }
        }

        [DataMember(Order = 1)]
        public string ExePath
        {
            get { return _exePath; }
            set { SetField(ref _exePath, value, "ExePath"); }
        }

        [DataMember(Order = 2)]
        public FixedPriority Priority
        {
            get { return _priority; }
            set { SetField(ref _priority, value, "Priority"); }
        }

        [DataMember(Order = 3)]
        public bool RunOnStartupWithLock
        {
            get { return _runOnStartupWithLock; }
            set { SetField(ref _runOnStartupWithLock, value, "RunOnStartupWithLock"); }
        }

        [IgnoreDataMember]
        public string RuntimeStatus
        {
            get { return _runtimeStatus; }
            set { SetField(ref _runtimeStatus, value, "RuntimeStatus"); }
        }

        [IgnoreDataMember]
        public int? ProcessId
        {
            get { return _processId; }
            set { SetField(ref _processId, value, "ProcessId"); }
        }

        [IgnoreDataMember]
        public bool IsPriorityLocked
        {
            get { return _isPriorityLocked; }
            set { SetField(ref _isPriorityLocked, value, "IsPriorityLocked"); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void SetField<T>(ref T field, T value, string propertyName)
        {
            if (object.Equals(field, value))
            {
                return;
            }

            field = value;
            OnPropertyChanged(propertyName);
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
