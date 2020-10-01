﻿using System;
using System.ComponentModel;
using System.Windows.Forms;
using Secs4Net;
using System.Net;
using System.Drawing;
using Secs4Net.Sml;
using Secs4Net.Exceptions;

namespace SecsDevice
{
    public partial class Form1 : Form {
        SecsGem _secsGem;
        readonly ISecsGemLogger _logger;
        readonly BindingList<PrimaryMessageWrapper> recvBuffer = new BindingList<PrimaryMessageWrapper>();

        public Form1() {
            InitializeComponent();

            radioActiveMode.DataBindings.Add("Enabled", btnEnable, "Enabled");
            radioPassiveMode.DataBindings.Add("Enabled", btnEnable, "Enabled");
            txtAddress.DataBindings.Add("Enabled", btnEnable, "Enabled");
            numPort.DataBindings.Add("Enabled", btnEnable, "Enabled");
            numDeviceId.DataBindings.Add("Enabled", btnEnable, "Enabled");
            numBufferSize.DataBindings.Add("Enabled", btnEnable, "Enabled");
            recvMessageBindingSource.DataSource = recvBuffer;
            Application.ThreadException += (sender, e) => MessageBox.Show(e.Exception.ToString());
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => MessageBox.Show(e.ExceptionObject.ToString());
            _logger = new SecsLogger(this);
        }

        private void btnEnable_Click(object sender, EventArgs e)
        {
            _secsGem?.Dispose();
            _secsGem = new SecsGem(
                radioActiveMode.Checked,
                IPAddress.Parse(txtAddress.Text),
                (ushort)(int)numPort.Value,
                (int)numBufferSize.Value)
            { Logger = _logger, DeviceId = (ushort)numDeviceId.Value };

            _secsGem.ConnectionChanged += delegate
            {
                this.Invoke((MethodInvoker)delegate
                {
                    lbStatus.Text = _secsGem.State.ToString();
                });
            };

            _secsGem.PrimaryMessageReceived += PrimaryMessageReceived;

            btnEnable.Enabled = false;
            _secsGem.Start();
            btnDisable.Enabled = true;
        }

        private void PrimaryMessageReceived(object sender, PrimaryMessageWrapper e)
        {
            this.Invoke(new MethodInvoker(() => recvBuffer.Add(e)));
        }

        private void btnDisable_Click(object sender, EventArgs e)
        {
            _secsGem?.Dispose();
            _secsGem = null;
            btnEnable.Enabled = true;
            btnDisable.Enabled = false;
            lbStatus.Text = "Disable";
            recvBuffer.Clear();
            richTextBox1.Clear();
        }

        private async void btnSendPrimary_Click(object sender, EventArgs e)
        {
            if (_secsGem.State != ConnectionState.Selected)
                return;
            if (string.IsNullOrWhiteSpace(txtSendPrimary.Text))
                return;

            try
            {
                var reply = await _secsGem.SendAsync(txtSendPrimary.Text.ToSecsMessage());
                txtRecvSecondary.Text = reply.ToSml();
            }
            catch (SecsException ex)
            {
                txtRecvSecondary.Text = ex.Message;
            }
        }

        private void lstUnreplyMsg_SelectedIndexChanged(object sender, EventArgs e) {
            var receivedMessage = lstUnreplyMsg.SelectedItem as PrimaryMessageWrapper;
            txtRecvPrimary.Text = receivedMessage?.Message.ToSml();
        }

        private async void btnReplySecondary_Click(object sender, EventArgs e)
        {
            if (!(lstUnreplyMsg.SelectedItem is PrimaryMessageWrapper recv))
                return;

            if (string.IsNullOrWhiteSpace(txtReplySeconary.Text))
                return;

            await recv.ReplyAsync(txtReplySeconary.Text.ToSecsMessage());
            recvBuffer.Remove(recv);
            txtRecvPrimary.Clear();
        }

        private async void btnReplyS9F7_Click(object sender, EventArgs e)
        {
            var recv = lstUnreplyMsg.SelectedItem as PrimaryMessageWrapper;
            if (recv == null)
                return;

            await recv.ReplyAsync(null);

            recvBuffer.Remove(recv);
            txtRecvPrimary.Clear();
        }

        class SecsLogger : ISecsGemLogger
        {
            readonly Form1 _form;
            internal SecsLogger(Form1 form)
            {
                _form = form;
            }
            public void MessageIn(SecsMessage msg, int systembyte)
            {
                _form.Invoke((MethodInvoker)delegate {
                    _form.richTextBox1.SelectionColor = Color.Black;
                    _form.richTextBox1.AppendText($"<-- [0x{systembyte:X8}] {msg.ToSml()}\n");
                });
            }

            public void MessageOut(SecsMessage msg, int systembyte)
            {
                _form.Invoke((MethodInvoker)delegate {
                    _form.richTextBox1.SelectionColor = Color.Black;
                    _form.richTextBox1.AppendText($"--> [0x{systembyte:X8}] {msg.ToSml()}\n");
                });
            }

            public void Info(string msg)
            {
                _form.Invoke((MethodInvoker)delegate {
                    _form.richTextBox1.SelectionColor = Color.Blue;
                    _form.richTextBox1.AppendText($"{msg}\n");
                });
            }

            public void Warning(string msg)
            {
                _form.Invoke((MethodInvoker)delegate {
                    _form.richTextBox1.SelectionColor = Color.Green;
                    _form.richTextBox1.AppendText($"{msg}\n");
                });
            }

            public void Error(string msg, Exception ex = null)
            {
                _form.Invoke((MethodInvoker)delegate {
                    _form.richTextBox1.SelectionColor = Color.Red;
                    _form.richTextBox1.AppendText($"{msg}\n");
                    _form.richTextBox1.SelectionColor = Color.Gray;
                    _form.richTextBox1.AppendText($"{ex}\n");
                });
            }

            public void Debug(string msg)
            {
                _form.Invoke((MethodInvoker)delegate {
                    _form.richTextBox1.SelectionColor = Color.Yellow;
                    _form.richTextBox1.AppendText($"{msg}\n");
                });
            }

            public void ResetTheConnection()
            {
            }
        }
    }
}
