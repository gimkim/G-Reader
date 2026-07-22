namespace CDisplayEx.CSharp;

internal sealed class DeletePdfPageDialog : Form
{
    private readonly List<RadioButton> _pageChoices = [];
    private readonly Bitmap[] _previews;
    private readonly int[] _pageIndices;
    private readonly bool _chooseOnePage;

    public int[] SelectedPageIndices
    {
        get
        {
            if (!_chooseOnePage) return _pageIndices.ToArray();
            var selected = _pageChoices.FindIndex(choice => choice.Checked);
            return [selected >= 0 ? _pageIndices[selected] : _pageIndices[0]];
        }
    }

    public DeletePdfPageDialog(string pdfPath, IReadOnlyList<int> pageIndices,
        IReadOnlyList<Bitmap> previews, bool chooseOnePage)
    {
        if (pageIndices.Count < 1 || pageIndices.Count != previews.Count)
            throw new ArgumentException("Matching PDF pages and previews are required.");
        if (chooseOnePage && pageIndices.Count != 2)
            throw new ArgumentException("Page choice requires exactly two pages.");

        _pageIndices = pageIndices.ToArray();
        _previews = previews.ToArray();
        _chooseOnePage = chooseOnePage;
        var multipleDelete = !chooseOnePage && pageIndices.Count > 1;
        Text = chooseOnePage ? "Choose PDF page to delete" :
            multipleDelete ? "Delete PDF pages" : "Delete PDF page";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(31, 34, 40);
        ForeColor = Color.WhiteSmoke;
        ClientSize = new Size(pageIndices.Count == 1 ? 560 : 940, 680);

        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(18, 14, 18, 4),
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Color.White,
            Text = chooseOnePage
                ? "Which page in this spread do you want to delete?"
                : multipleDelete
                    ? $"Delete these {pageIndices.Count:N0} pages?"
                    : $"Delete page {pageIndices[0] + 1}?"
        };
        var fileLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(20, 2, 20, 8),
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.Silver,
            AutoEllipsis = true,
            Text = Path.GetFileName(pdfPath)
        };

        var previewPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 6, 14, 12),
            AutoScroll = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = BackColor
        };
        var cardWidth = pageIndices.Count == 1 ? 500 :
            pageIndices.Count == 2 ? 430 : 280;
        var cardHeight = pageIndices.Count <= 2 ? 490 : 360;
        for (var i = 0; i < pageIndices.Count; i++)
            previewPanel.Controls.Add(CreatePreviewCard(i, cardWidth, cardHeight));

        var warning = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(20, 7, 20, 4),
            ForeColor = Color.FromArgb(255, 205, 120),
            Text = "This changes the original PDF file. The operation cannot be undone in G Reader."
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(39, 42, 49)
        };
        var deleteButton = new Button
        {
            Text = chooseOnePage ? "Delete selected page" :
                multipleDelete ? $"Delete {pageIndices.Count:N0} pages" : "Delete page",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Height = 34,
            Padding = new Padding(12, 0, 12, 0),
            BackColor = Color.FromArgb(178, 58, 58),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        deleteButton.FlatAppearance.BorderColor = Color.FromArgb(220, 90, 90);
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Height = 34,
            Padding = new Padding(12, 0, 12, 0),
            BackColor = Color.FromArgb(58, 62, 72),
            ForeColor = Color.WhiteSmoke,
            FlatStyle = FlatStyle.Flat
        };
        buttons.Controls.Add(deleteButton);
        buttons.Controls.Add(cancelButton);

        Controls.Add(previewPanel);
        Controls.Add(warning);
        Controls.Add(fileLabel);
        Controls.Add(heading);
        Controls.Add(buttons);
        AcceptButton = deleteButton;
        CancelButton = cancelButton;
    }

    private Control CreatePreviewCard(int index, int width, int height)
    {
        Control caption;
        if (_chooseOnePage)
        {
            var radio = new RadioButton
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                TextAlign = ContentAlignment.MiddleCenter,
                CheckAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10f),
                Text = $"Page {_pageIndices[index] + 1}",
                Checked = index == 0
            };
            _pageChoices.Add(radio);
            radio.CheckedChanged += (_, _) =>
            {
                if (!radio.Checked) return;
                foreach (var other in _pageChoices)
                    if (!ReferenceEquals(other, radio)) other.Checked = false;
            };
            caption = radio;
        }
        else
        {
            caption = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 34,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10f),
                Text = $"Page {_pageIndices[index] + 1}"
            };
        }

        var picture = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = _previews[index],
            BackColor = Color.FromArgb(22, 24, 29),
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = _chooseOnePage ? Cursors.Hand : Cursors.Default
        };
        if (_chooseOnePage && caption is RadioButton choice)
            picture.Click += (_, _) => choice.Checked = true;
        var card = new Panel
        {
            Size = new Size(width, height),
            Margin = new Padding(8),
            Padding = new Padding(8),
            BackColor = BackColor
        };
        card.Controls.Add(picture);
        card.Controls.Add(caption);
        return card;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var preview in _previews) preview.Dispose();
        base.Dispose(disposing);
    }
}
