////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// ThreadSolidModeler 3D Print command
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using Inventor;
using ThreadModeler.Addin;
using ThreadModeler.Utilities;

namespace ThreadModeler.Commands
{
    internal class ThreadModeler3DPrintCmd : AdnButtonCommandBase
    {
        public ThreadModeler3DPrintCmd(Inventor.Application Application)
            : base(Application)
        {
        }

        public override string DisplayName
        {
            get { return "ThreadSolidModeler 3D Print"; }
        }

        public override string InternalName
        {
            get { return "ThreadSolidModeler.ThreadModeler3DPrintCmd"; }
        }

        public override CommandTypesEnum Classification
        {
            get { return CommandTypesEnum.kEditMaskCmdType; }
        }

        public override string ClientId
        {
            get
            {
                Type t = typeof(StandardAddInServer);
                return t.GUID.ToString("B");
            }
        }

        public override string Description
        {
            get { return "Displays the ThreadSolidModeler 3D Print control"; }
        }

        public override string ToolTipText
        {
            get { return "Displays the ThreadSolidModeler 3D Print control"; }
        }

        public override ButtonDisplayEnum ButtonDisplay
        {
            get { return ButtonDisplayEnum.kDisplayTextInLearningMode; }
        }

        public override string StandardIconName
        {
            get { return "ThreadModeler.resources.threadModeler.ico"; }
        }

        public override string LargeIconName
        {
            get { return "ThreadModeler.resources.threadModeler.ico"; }
        }

        protected override void OnExecute(NameValueMap context)
        {
            RegisterCommandForm(new PrintController(Application, InteractionManager), false);

            InteractionManager.AddPreSelectionFilter(ObjectTypeEnum.kThreadFeatureObject);
            InteractionManager.AddPreSelectionFilter(ObjectTypeEnum.kThreadFeatureProxyObject);
            InteractionManager.Start("Select one ThreadFeature for 3D Print");
        }

        protected override void OnHelp(NameValueMap context)
        {
        }

        protected override void OnLinearMarkingMenu(
            ObjectsEnumerator SelectedEntities,
            SelectionDeviceEnum SelectionDevice,
            CommandControls LinearMenu,
            NameValueMap AdditionalInfo)
        {
        }

        protected override void OnRadialMarkingMenu(
            ObjectsEnumerator SelectedEntities,
            SelectionDeviceEnum SelectionDevice,
            RadialMarkingMenu RadialMenu,
            NameValueMap AdditionalInfo)
        {
        }
    }
}
