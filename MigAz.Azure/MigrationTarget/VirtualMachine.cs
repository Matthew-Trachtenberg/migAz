// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using MigAz.Azure.Interface;
using MigAz.Core.Interface;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MigAz.Azure.Arm;
using MigAz.Core;
using MigAz.Core.ArmTemplate;

namespace MigAz.Azure.MigrationTarget
{
    public class VirtualMachine : Core.MigrationTarget
    {
        private AvailabilitySet _TargetAvailabilitySet = null;
        private Arm.VMSize _TargetSize;
        private List<NetworkInterface> _NetworkInterfaces = new List<NetworkInterface>();
        private List<Disk> _DataDisks = new List<Disk>();
        private Dictionary<string, string> _PlanAttributes;

        #region Constructors

        private VirtualMachine() : base(ArmConst.MicrosoftCompute, ArmConst.VirtualMachines) { }

        public VirtualMachine(Asm.VirtualMachine virtualMachine, TargetSettings targetSettings) : base(ArmConst.MicrosoftCompute, ArmConst.VirtualMachines)
        {
            this.Source = virtualMachine;
            this.SetTargetName(virtualMachine.RoleName, targetSettings);
            this.OSVirtualHardDisk = new Disk(virtualMachine.OSVirtualHardDisk, this, targetSettings);
            this.OSVirtualHardDiskOS = virtualMachine.OSVirtualHardDiskOS;

            if (targetSettings.DefaultTargetDiskType == ArmDiskType.ClassicDisk)
                this.OSVirtualHardDisk.TargetStorage = SeekTargetStorageAccount(virtualMachine.AzureSubscription.AsmTargetStorageAccounts, virtualMachine.OSVirtualHardDisk.StorageAccountName);

            foreach (Asm.Disk asmDataDisk in virtualMachine.DataDisks)
            {
                Disk targetDataDisk = new Disk(asmDataDisk, this, targetSettings);

                EnsureDataDiskTargetLunIsNotNull(ref targetDataDisk);

                if (targetSettings.DefaultTargetDiskType == ArmDiskType.ClassicDisk)
                    targetDataDisk.TargetStorage = SeekTargetStorageAccount(virtualMachine.AzureSubscription.AsmTargetStorageAccounts, asmDataDisk.StorageAccountName);

                this.DataDisks.Add(targetDataDisk);
            }

            foreach (Asm.NetworkInterface asmNetworkInterface in virtualMachine.NetworkInterfaces)
            {
                NetworkInterface migrationNetworkInterface = new NetworkInterface(virtualMachine, asmNetworkInterface, virtualMachine.AzureSubscription.AsmTargetVirtualNetworks, virtualMachine.AzureSubscription.AsmTargetNetworkSecurityGroups, targetSettings);
                migrationNetworkInterface.ParentVirtualMachine = this;
                this.NetworkInterfaces.Add(migrationNetworkInterface);
            }

            #region Seek ARM Target Size

            // Get ARM Based Location (that matches location of Source ASM VM
            Arm.Location armLocation = virtualMachine.AzureSubscription.GetAzureARMLocation(virtualMachine.Location);
            if (armLocation != null)
            {
                this.TargetSize = armLocation.SeekVmSize(virtualMachine.RoleSize.Name);

                if (this.TargetSize == null)
                {
                    // if not found, defer to alternate matching options

                    Dictionary<string, string> VMSizeTable = new Dictionary<string, string>();
                    VMSizeTable.Add("ExtraSmall", "Standard_A0");
                    VMSizeTable.Add("Small", "Standard_A1");
                    VMSizeTable.Add("Medium", "Standard_A2");
                    VMSizeTable.Add("Large", "Standard_A3");
                    VMSizeTable.Add("ExtraLarge", "Standard_A4");
                    VMSizeTable.Add("A5", "Standard_A5");
                    VMSizeTable.Add("A6", "Standard_A6");
                    VMSizeTable.Add("A7", "Standard_A7");
                    VMSizeTable.Add("A8", "Standard_A8");
                    VMSizeTable.Add("A9", "Standard_A9");
                    VMSizeTable.Add("A10", "Standard_A10");
                    VMSizeTable.Add("A11", "Standard_A11");

                    if (VMSizeTable.ContainsKey(virtualMachine.RoleSize.Name))
                    {
                        this.TargetSize = armLocation.SeekVmSize(VMSizeTable[virtualMachine.RoleSize.Name]);
                    }
                }
            }

            #endregion
        }

        public VirtualMachine(Arm.VirtualMachine virtualMachine, TargetSettings targetSettings) : base(ArmConst.MicrosoftCompute, ArmConst.VirtualMachines)
        {
            this.Source = virtualMachine;
            this.SetTargetName(virtualMachine.Name, targetSettings);
            this.TargetSize = virtualMachine.VmSize;
            this.OSVirtualHardDiskOS = virtualMachine.OSVirtualHardDiskOS;

            if (virtualMachine.OSVirtualHardDisk != null && virtualMachine.OSVirtualHardDisk.GetType() == typeof(Azure.Arm.ManagedDisk))
            {
                Azure.Arm.ManagedDisk sourceManagedDisk = (Azure.Arm.ManagedDisk)virtualMachine.OSVirtualHardDisk;

                foreach (Disk targetDisk in virtualMachine.AzureSubscription.ArmTargetManagedDisks)
                {
                    if ((targetDisk.SourceDisk != null) && (targetDisk.SourceDisk.GetType() == typeof(Azure.Arm.ManagedDisk)))
                    {
                        Azure.Arm.ManagedDisk targetDiskSourceDisk = (Azure.Arm.ManagedDisk)targetDisk.SourceDisk;
                        if (String.Compare(targetDiskSourceDisk.Name, sourceManagedDisk.Name, true) == 0)
                        {
                            this.OSVirtualHardDisk = targetDisk;
                            targetDisk.ParentVirtualMachine = this;
                            targetDisk.HostCaching = sourceManagedDisk.HostCaching;
                            break;
                        }
                    }
                }
            }
            else
            {
                if (virtualMachine.OSVirtualHardDisk != null)
                {
                    this.OSVirtualHardDisk = new Disk(virtualMachine.OSVirtualHardDisk, this, targetSettings);
                }
            }

            if (virtualMachine.OSVirtualHardDisk != null && virtualMachine.OSVirtualHardDisk.GetType() == typeof(Arm.ClassicDisk))
            {
                Arm.ClassicDisk armDisk = (Arm.ClassicDisk)virtualMachine.OSVirtualHardDisk;
                if (targetSettings.DefaultTargetDiskType == ArmDiskType.ClassicDisk)
                    this.OSVirtualHardDisk.TargetStorage = SeekTargetStorageAccount(virtualMachine.AzureSubscription.ArmTargetStorageAccounts, armDisk.StorageAccountName);
            }

            foreach (IArmDisk dataDisk in virtualMachine.DataDisks)
            {
                if (dataDisk.GetType() == typeof(Azure.Arm.ManagedDisk))
                {
                    Azure.Arm.ManagedDisk sourceManagedDisk = (Azure.Arm.ManagedDisk)dataDisk;
                    MigrationTarget.Disk targetDataDisk = null;

                    foreach (Disk targetDisk in virtualMachine.AzureSubscription.ArmTargetManagedDisks)
                    {
                        if ((targetDisk.SourceDisk != null) && (targetDisk.SourceDisk.GetType() == typeof(Azure.Arm.ManagedDisk)))
                        {
                            Azure.Arm.ManagedDisk targetDiskSourceDisk = (Azure.Arm.ManagedDisk)targetDisk.SourceDisk;
                            if (String.Compare(targetDiskSourceDisk.Name, sourceManagedDisk.Name, true) == 0)
                            {
                                targetDataDisk = targetDisk;
                                break;
                            }
                        }
                    }

                    if (targetDataDisk != null)
                    {
                        EnsureDataDiskTargetLunIsNotNull(ref targetDataDisk);
                        targetDataDisk.ParentVirtualMachine = this;
                        targetDataDisk.Lun = sourceManagedDisk.Lun;
                        targetDataDisk.HostCaching = sourceManagedDisk.HostCaching;

                        this.DataDisks.Add(targetDataDisk);
                    }
                }
                else if(dataDisk.GetType() == typeof(Arm.ClassicDisk))
                {
                    Disk targetDataDisk = new Disk(dataDisk, this, targetSettings);

                    Arm.ClassicDisk armDisk = (Arm.ClassicDisk)dataDisk;
                    if (targetSettings.DefaultTargetDiskType == ArmDiskType.ClassicDisk)
                        targetDataDisk.TargetStorage = SeekTargetStorageAccount(virtualMachine.AzureSubscription.ArmTargetStorageAccounts, armDisk.StorageAccountName);

                    EnsureDataDiskTargetLunIsNotNull(ref targetDataDisk);

                    this.DataDisks.Add(targetDataDisk);
                }
            }

            foreach (Arm.NetworkInterface armNetworkInterface in virtualMachine.NetworkInterfaces)
            {
                foreach (NetworkInterface targetNetworkInterface in virtualMachine.AzureSubscription.ArmTargetNetworkInterfaces)
                {
                    if ((targetNetworkInterface.SourceNetworkInterface != null) && (targetNetworkInterface.SourceNetworkInterface.GetType() == typeof(Azure.Arm.NetworkInterface)))
                    {
                        Azure.Arm.NetworkInterface targetNetworkInterfaceSourceInterface = (Azure.Arm.NetworkInterface)targetNetworkInterface.SourceNetworkInterface;
                        if (String.Compare(targetNetworkInterfaceSourceInterface.Name, armNetworkInterface.Name, true) == 0)
                        {
                            this.NetworkInterfaces.Add(targetNetworkInterface);
                            targetNetworkInterface.ParentVirtualMachine = this;
                            break;
                        }
                    }
                }

                
            }

            if (virtualMachine.HasPlan)
            {
                _PlanAttributes = new Dictionary<string, string>();

                foreach (JProperty planAttribute in virtualMachine.ResourceToken["plan"])
                {
                    _PlanAttributes.Add(planAttribute.Name, planAttribute.Value.ToString());
                }
            }
        }

        #endregion

        private void EnsureDataDiskTargetLunIsNotNull(ref Disk targetDataDisk)
        {
            if (!targetDataDisk.Lun.HasValue)
            {
                // Every Data Disk should have a Lun Index already assigned from the ASM XML.  In the event it does not have a value, we are going to 
                // change the LUN from Null to -1 to indicate the disks is a data disk, but the LUN could not be determined.  Given that -1 is not an 
                // allowed LUN Index, we'll use -1 in the tool to identify that this Disk is a Data Disk, is missing LUN index, require user to specify index.
                targetDataDisk.Lun = -1;
            }
        }

        private StorageAccount SeekTargetStorageAccount(List<StorageAccount> storageAccounts, string sourceAccountName)
        {
            foreach (StorageAccount targetStorageAccount in storageAccounts)
            {
                if (targetStorageAccount.SourceName == sourceAccountName)
                    return targetStorageAccount;
            }

            return null;
        }

        public Disk OSVirtualHardDisk
        {
            get; set;
        }

        public List<Disk> DataDisks
        {
            get { return _DataDisks; }
        }

        public IVirtualMachine Source { get; set; }

        public List<NetworkInterface> NetworkInterfaces
        {
            get { return _NetworkInterfaces; }
        }

        public NetworkInterface PrimaryNetworkInterface
        {
            get
            {
                foreach (NetworkInterface networkInterface in this.NetworkInterfaces)
                {
                    if (networkInterface.IsPrimary)
                        return networkInterface;
                }

                return null;
            }
        }

        public Arm.VMSize TargetSize
        {
            get { return _TargetSize; }
            set { _TargetSize = value; }
        }


        public Dictionary<string, string> PlanAttributes
        {
            get { return _PlanAttributes; }
            set { _PlanAttributes = value; }
        }

        public string SourceName
        {
            get
            {
                if (this.Source == null)
                    return String.Empty;
                else
                    return this.Source.ToString();
            }
        }

        public string OSVirtualHardDiskOS
        {
            get; set;
        }

        public AvailabilitySet TargetAvailabilitySet
        {
            get { return _TargetAvailabilitySet; }
            set
            {
                if (value != _TargetAvailabilitySet)
                {
                    if (_TargetAvailabilitySet != null)
                    {
                        _TargetAvailabilitySet.TargetVirtualMachines.Remove(this);
                    }

                    _TargetAvailabilitySet = value;

                    if (_TargetAvailabilitySet != null)
                    {
                        if (!_TargetAvailabilitySet.TargetVirtualMachines.Contains(this))
                            _TargetAvailabilitySet.TargetVirtualMachines.Add(this);
                    }
                }
            }
        }

        public bool IsManagedDisks
        {
            get
            {
                if (this.OSVirtualHardDisk != null && !this.OSVirtualHardDisk.IsManagedDisk)
                    return false;

                foreach (Azure.MigrationTarget.Disk dataDisk in this.DataDisks)
                {
                    if (!dataDisk.IsManagedDisk)
                        return false;
                }

                return true;
            }
        }

        public bool IsUnmanagedDisks
        {
            get
            {
                if (this.OSVirtualHardDisk != null && !this.OSVirtualHardDisk.IsUnmanagedDisk)
                    return false;

                foreach (Azure.MigrationTarget.Disk dataDisk in this.DataDisks)
                {
                    if (!dataDisk.IsUnmanagedDisk)
                        return false;
                }

                return true;
            }
        }

        public override string ImageKey { get { return "VirtualMachine"; } }

        public override string FriendlyObjectName { get { return "Virtual Machine"; } }


        public override void SetTargetName(string targetName, TargetSettings targetSettings)
        {
            this.TargetName = targetName.Trim().Replace(" ", String.Empty);
            this.TargetNameResult = this.TargetName + targetSettings.VirtualMachineSuffix;
        }
    }
}

