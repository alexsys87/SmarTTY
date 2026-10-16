using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class DynamicTreeBrowser : UserControl
{
    public interface IChildLoader
    {
        bool IsInstant { get; }

        string StatusText { get; }

        double Progress { get; }

        void Cancel();

        IBrowserItem[] LoadChildren(bool flag);
    }

    public interface IBrowserView
    {
        string Title { get; }

        Image Icon { get; }

        string GetItemText(IBrowserItem browserItem);
    }

    public interface IBrowserItem
    {
        string Text { get; }

        int ImageIndex { get; }

        ContextMenuStrip ContextMenu { get; }

        IChildLoader GetChildLoader(int value2);

        void Activate();
    }

    public interface IBrowserModel
    {
        ImageList Images { get; }

        IBrowserView[] Views { get; }

        IChildLoader GetRootLoader();
    }

    private class ViewToggleButton : ToolStripButton
    {
        public readonly int ViewIndex;

        public readonly IBrowserView View;

        private DynamicTreeBrowser _browser;

        public int TreeImageIndex = -1;

        public ViewToggleButton(int value2, IBrowserView browserView, DynamicTreeBrowser dynamicTreeBrowser)
            : base(browserView.Title, browserView.Icon)
        {
            this.ViewIndex = value2;
            View = browserView;
            Image = browserView.Icon;
            CheckOnClick = true;
            Checked = true;
            this._browser = dynamicTreeBrowser;
            if (Image == null)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text;
            }
            else
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image;
            }
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (_browser == null)
            {
                return;
            }
            List<ViewToggleButton> list = new List<ViewToggleButton>();
            ViewToggleButton[] array = _browser._viewButtons;
            if (array != null)
            {
                ViewToggleButton[] array2 = array;
                foreach (ViewToggleButton viewToggleButton in array2)
                {
                    if (viewToggleButton.Checked)
                    {
                        list.Add(viewToggleButton);
                    }
                }
            }
            if (list.Count == 0)
            {
                Checked = true;
            }
            else if (!Checked || list.Count != 1)
            {
                _browser.UpdateViewNodes(list.ToArray(), this, _browser.treeView1.Nodes);
            }
        }
    }

    public delegate void ViewsChangedHandler(bool flag, ToolStripButton[] items);

    private class LoadingNode : TreeNode
    {
        public LoadingNode()
            : base("Loading...")
        {
        }
    }

    private class ViewNode : TreeNode
    {
        public readonly IBrowserView View;

        public readonly int ViewIndex;

        public readonly IBrowserItem Item;

        public ViewNode(IBrowserView browserView, IBrowserItem browserItem, int value2, int value3)
            : base(browserView.GetItemText(browserItem))
        {
            this.View = browserView;
            this.ViewIndex = value2;
            this.Item = browserItem;
            ImageIndex = (SelectedImageIndex = value3);
            Nodes.Add(new LoadingNode());
        }
    }

    private class ItemNode : TreeNode
    {
        public readonly IBrowserItem Item;

        public int SoleViewIndex = -1;

        public ItemNode(IBrowserItem browserItem, ViewToggleButton[] items)
            : base(browserItem.Text, browserItem.ImageIndex, browserItem.ImageIndex)
        {
            Item = browserItem;
            List<ViewNode> list = new List<ViewNode>();
            if (items != null)
            {
                foreach (ViewToggleButton viewToggleButton in items)
                {
                    if (viewToggleButton.Checked)
                    {
                        list.Add(new ViewNode(viewToggleButton.View, browserItem, viewToggleButton.ViewIndex, viewToggleButton.TreeImageIndex));
                    }
                }
            }
            if (list.Count == 1)
            {
                this.SoleViewIndex = list[0].ViewIndex;
            }
            if (list.Count > 1)
            {
                TreeNodeCollection treeNodeCollection = Nodes;
                TreeNode[] array = list.ToArray();
                treeNodeCollection.AddRange(array);
            }
            else
            {
                Nodes.Add(new LoadingNode());
            }
        }

        public void UpdateViews(ViewToggleButton[] items, ViewToggleButton viewToggleButton)
        {
            if (items.Length == 1)
            {
                ViewNode viewNode = null;
                foreach (TreeNode node3 in Nodes)
                {
                    if (node3 is ViewNode && (node3 as ViewNode).ViewIndex == items[0].ViewIndex)
                    {
                        viewNode = node3 as ViewNode;
                        break;
                    }
                }
                Nodes.Clear();
                if (viewNode == null)
                {
                    Collapse();
                    Nodes.Add(new LoadingNode());
                    return;
                }
                this.SoleViewIndex = viewNode.ViewIndex;
                while (viewNode.Nodes.Count > 0)
                {
                    TreeNode node = viewNode.Nodes[0];
                    viewNode.Nodes.RemoveAt(0);
                    Nodes.Add(node);
                }
                if (viewNode.IsExpanded)
                {
                    Expand();
                }
                else
                {
                    Collapse();
                }
                return;
            }
            if (this.SoleViewIndex != -1)
            {
                List<TreeNode> list = new List<TreeNode>();
                for (int num = 0; num < Nodes.Count; num++)
                {
                    if (Nodes[num] is ItemNode || Nodes[num] is LoadingNode)
                    {
                        list.Add(Nodes[num]);
                        Nodes.RemoveAt(num--);
                    }
                }
                ViewToggleButton viewToggleButton2 = null;
                foreach (ViewToggleButton viewToggleButton3 in items)
                {
                    if (viewToggleButton3.ViewIndex == this.SoleViewIndex)
                    {
                        viewToggleButton2 = viewToggleButton3;
                        break;
                    }
                }
                if (viewToggleButton2 != null)
                {
                    ViewNode viewNode2 = new ViewNode(viewToggleButton2.View, Item, viewToggleButton2.ViewIndex, viewToggleButton2.TreeImageIndex);
                    viewNode2.Nodes.Clear();
                    foreach (TreeNode item in list)
                    {
                        viewNode2.Nodes.Add(item);
                    }
                    Nodes.Add(viewNode2);
                }
                this.SoleViewIndex = -1;
            }
            if (viewToggleButton.Checked)
            {
                int num3 = 0;
                for (int num4 = 0; num4 < Nodes.Count; num4++)
                {
                    if (Nodes[num4] is ViewNode && (Nodes[num4] as ViewNode).ViewIndex < viewToggleButton.ViewIndex)
                    {
                        num3 = num4 + 1;
                    }
                }
                ViewNode node2 = new ViewNode(viewToggleButton.View, Item, viewToggleButton.ViewIndex, viewToggleButton.TreeImageIndex);
                Nodes.Insert(num3, node2);
                return;
            }
            for (int num5 = 0; num5 < Nodes.Count; num5++)
            {
                if (Nodes[num5] is ViewNode && (Nodes[num5] as ViewNode).ViewIndex == viewToggleButton.ViewIndex)
                {
                    Nodes.RemoveAt(num5);
                    break;
                }
            }
        }
    }

    private class LoadOperation
    {
        private IChildLoader _loader;

        private Thread _thread;

        private IBrowserItem[] _items;

        private Exception _exception;

        private TreeNodeCollection _targetNodes;

        private TreeNode _parentNode;

        public LoadOperation(TreeNodeCollection treeNodeCollection, IChildLoader childLoader, TreeNode treeNode)
        {
            _loader = childLoader;
            _targetNodes = treeNodeCollection;
            _parentNode = treeNode;
            this._thread = new Thread(ThreadBody);
            this._thread.Start();
        }

        private void ThreadBody()
        {
            try
            {
                _items = _loader.LoadChildren(flag: false);
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
        }

        public bool Wait(int value2)
        {
            return _thread.Join(value2);
        }

        public void Complete(ViewToggleButton[] items)
        {
            if (_exception != null)
            {
                if (_parentNode != null)
                {
                    _parentNode.Collapse();
                }
                throw new TargetInvocationException(_exception);
            }
            _targetNodes.Clear();
            if (_items != null)
            {
                IBrowserItem[] array = _items;
                foreach (IBrowserItem browserItem in array)
                {
                    _targetNodes.Add(new ItemNode(browserItem, items));
                }
            }
        }

        public void CancelLoad()
        {
            if (_thread.IsAlive)
            {
                _loader.Cancel();
            }
        }

        internal KeyValuePair<string, double> GetProgress()
        {
            return new KeyValuePair<string, double>(_loader.StatusText, _loader.Progress);
        }
    }

    public delegate void LoadFailedHandler(Exception exception, ref bool flag);

    public class StaticChildLoader : IChildLoader
    {
        private IBrowserItem[] _items;

        public bool IsInstant => true;

        public string StatusText => null;

        public double Progress => -1.0;

        public StaticChildLoader(IEnumerable<IBrowserItem> items)
        {
            List<IBrowserItem> list = new List<IBrowserItem>();
            foreach (IBrowserItem item in items)
            {
                list.Add(item);
            }
            _items = list.ToArray();
        }

        public void Cancel()
        {
        }

        public IBrowserItem[] LoadChildren(bool flag)
        {
            return _items;
        }
    }

    public delegate void ProgressHandler(bool flag, string text, double value2);

    private ViewToggleButton[] _viewButtons;

    private LoadOperation _currentOperation;

    public IntPtr TreeHandle => treeView1.Handle;

    private LoadOperation CurrentOperation
    {
        get
        {
            return _currentOperation;
        }
        set
        {
            if (_currentOperation != null)
            {
                _currentOperation.CancelLoad();
            }
            _currentOperation = value;
            _timer.Enabled = value != null;
            treeView1.Enabled = value == null;
            if (ProgressChanged != null)
            {
                ProgressChanged(value != null, null, -1.0);
            }
            ViewToggleButton[] array = _viewButtons;
            if (array != null)
            {
                ViewToggleButton[] array2 = array;
                for (int num = 0; num < array2.Length; num++)
                {
                    array2[num].Enabled = value == null;
                }
            }
        }
    }

    public event ViewsChangedHandler ViewsChanged;

    public event LoadFailedHandler LoadFailed;

    public event ProgressHandler ProgressChanged;

    public DynamicTreeBrowser()
    {
        InitializeComponent();
    }

    public void SetModel(IBrowserModel browserModel)
    {
        treeView1.Nodes.Clear();
        CurrentOperation = null;
        if (_viewButtons != null)
        {
            ViewToggleButton[] array = _viewButtons;
            for (int num = 0; num < array.Length; num++)
            {
                array[num].Dispose();
            }
            _viewButtons = null;
        }
        if (browserModel == null)
        {
            Enabled = false;
            return;
        }
        ImageList imageList = browserModel.Images;
        if (imageList == null)
        {
            treeView1.ImageList = null;
        }
        else
        {
            ImageList imageList2 = new ImageList();
            imageList2.ImageSize = imageList.ImageSize;
            foreach (Image image in imageList.Images)
            {
                imageList2.Images.Add(image);
            }
            treeView1.ImageList = imageList2;
        }
        IBrowserView[] array2 = browserModel.Views;
        ToolStripButton[] array3;
        if (array2 == null)
        {
            _viewButtons = null;
            array3 = null;
        }
        else
        {
            ViewToggleButton[] array4 = new ViewToggleButton[array2.Length];
            array3 = new ToolStripButton[array2.Length];
            for (int num2 = 0; num2 < array2.Length; num2++)
            {
                ViewToggleButton viewToggleButton = (ViewToggleButton)(array3[num2] = (array4[num2] = new ViewToggleButton(num2, array2[num2], this)));
                if (treeView1.ImageList != null && array2[num2].Icon != null)
                {
                    treeView1.ImageList.Images.Add(array2[num2].Icon);
                    viewToggleButton.TreeImageIndex = treeView1.ImageList.Images.Count - 1;
                }
            }
            _viewButtons = array4;
        }
        LoadInto(treeView1.Nodes, browserModel.GetRootLoader(), null);
        if (ViewsChanged != null)
        {
            ViewsChanged(flag: true, array3);
        }
        if (treeView1.Nodes.Count > 0)
        {
            treeView1.SelectedNode = treeView1.Nodes[0];
            treeView1.Focus();
        }
        Enabled = true;
    }

    private void LoadInto(TreeNodeCollection treeNodeCollection, IChildLoader childLoader, TreeNode treeNode)
    {
        try
        {
            if (childLoader == null)
            {
                treeNodeCollection.Clear();
            }
            else if (childLoader.IsInstant)
            {
                IBrowserItem[] array = childLoader.LoadChildren(flag: true);
                treeNodeCollection.Clear();
                if (array != null)
                {
                    IBrowserItem[] array2 = array;
                    foreach (IBrowserItem browserItem in array2)
                    {
                        treeNodeCollection.Add(new ItemNode(browserItem, _viewButtons));
                    }
                }
            }
            else
            {
                LoadOperation loadOperation = new LoadOperation(treeNodeCollection, childLoader, treeNode);
                if (loadOperation.Wait(100))
                {
                    loadOperation.Complete(_viewButtons);
                    return;
                }
                CurrentOperation = loadOperation;
                OnTimerTick(null, null);
            }
        }
        catch (Exception ex)
        {
            this.ReportError(ex);
        }
    }

    private void ReportError(Exception exception)
    {
        bool flag = false;
        if (LoadFailed != null)
        {
            LoadFailed(exception, ref flag);
        }
        if (!flag)
        {
            if (exception is TargetInvocationException)
            {
                MessageBox.Show((exception as TargetInvocationException).InnerException.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            else
            {
                MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
        }
    }

    private void treeView1_AfterExpand(object sender, TreeViewEventArgs e)
    {
        if (e.Node is ItemNode)
        {
            ItemNode itemNode = e.Node as ItemNode;
            if (itemNode.Nodes.Count == 1 && itemNode.Nodes[0] is LoadingNode)
            {
                LoadInto(e.Node.Nodes, itemNode.Item.GetChildLoader(itemNode.SoleViewIndex), itemNode);
            }
        }
        else if (e.Node is ViewNode)
        {
            ViewNode viewNode = e.Node as ViewNode;
            if (viewNode.Nodes.Count == 1 && viewNode.Nodes[0] is LoadingNode)
            {
                LoadInto(e.Node.Nodes, viewNode.Item.GetChildLoader(viewNode.ViewIndex), viewNode);
            }
        }
        treeView1.Focus();
    }

    public void CancelLoading()
    {
        _currentOperation?.CancelLoad();
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        LoadOperation loadOperation = _currentOperation;
        if (loadOperation == null)
        {
            return;
        }
        if (loadOperation.Wait(0))
        {
            _timer.Enabled = false;
            CurrentOperation = null;
            try
            {
                loadOperation.Complete(_viewButtons);
                return;
            }
            catch (Exception ex)
            {
                this.ReportError(ex);
                return;
            }
        }
        KeyValuePair<string, double> keyValuePair = loadOperation.GetProgress();
        if (ProgressChanged != null)
        {
            ProgressChanged(flag: true, keyValuePair.Key, keyValuePair.Value);
        }
    }

    private void UpdateViewNodes(ViewToggleButton[] items, ViewToggleButton viewToggleButton, TreeNodeCollection treeNodeCollection)
    {
        foreach (TreeNode item in treeNodeCollection)
        {
            if (item is ItemNode)
            {
                (item as ItemNode).UpdateViews(items, viewToggleButton);
            }
            UpdateViewNodes(items, viewToggleButton, item.Nodes);
        }
    }

    private void treeView1_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e2)
    {
        if (e2.Node is ItemNode itemNode)
        {
            itemNode.Item.Activate();
        }
    }

    private void treeView1_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            if (!(e.Node is ItemNode itemNode))
            {
                treeView1.ContextMenuStrip = null;
            }
            else
            {
                treeView1.ContextMenuStrip = itemNode.Item.ContextMenu;
            }
        }
    }

    private void treeView1_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Return && treeView1.SelectedNode is ItemNode itemNode)
        {
            itemNode.Item.Activate();
        }
    }
}
