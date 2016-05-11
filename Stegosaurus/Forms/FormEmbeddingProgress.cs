﻿using Stegosaurus.Algorithm;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Stegosaurus.Carrier;

namespace Stegosaurus.Forms
{
    public partial class FormEmbeddingProgress : Form
    {
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        private string name, extension;
        private ICarrierMedia carrierMedia;

        public FormEmbeddingProgress()
        {
            InitializeComponent();
        }

        public async void Run(StegoMessage _message, StegoAlgorithmBase _algorithm, string _name, string _extension)
        {
            Progress<int> progress = new Progress<int>(p =>
            {
                if (p <= progressBarMain.Maximum)
                {
                    progressBarMain.Value = p;
                    Text = p + "%";
                }
            });

            // Await execution
            bool result = await Task.Run(() =>
            {
                try
                {
                    _algorithm.Embed(_message, progress, cts.Token);
                    name = _name;
                    extension = _extension;
                    carrierMedia = _algorithm.CarrierMedia;
                }
                catch (OperationCanceledException)
                {
                    // Form was closed
                    return false;
                }
                return true;
            });

            if (result)
            {
                SystemSounds.Hand.Play();
                labelStatus.Text = "Embedding complete!";
                buttonCancel.Enabled = false;
                buttonSaveAs.Enabled = true;
            }
        }

        private void FormEmbeddingProgress_FormClosing(object sender, FormClosingEventArgs e)
        {
            cts.Cancel();
        }

        private void buttonCancel_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void buttonSaveAs_Click(object sender, EventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.FileName = $"stego-{name}";
            sfd.Filter = $"Original extension (*{extension})|*{extension}|All files (*.*)|*.*";

            if (sfd.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            carrierMedia.SaveToFile(sfd.FileName);
        }

        private void FormEmbeddingProgress_Load(object sender, EventArgs e)
        {
            // Remove focus from cancel button
            Focus();
        }
    }
}
