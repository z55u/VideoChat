﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.ComponentModel;
using System.Windows.Threading;
using BDTP;

namespace VoiceChat.Model
{
    public class VoiceChatModel: INotifyPropertyChanged
    {
        // Подтверждения
        private enum Receipts
        {
            Accept
        }

        // Состояния модели
        public enum States
        {
            WaitCall,
            OutgoingCall,
            IncomingCall,
            Talk,
            Close
        }

        private BdtpClient bdtpClient;
        private Thread waitCall;
        private Thread receiveVoice;
        
        private WaveIn input;                       
        private WaveOut output;                     
        private BufferedWaveProvider bufferStream;  

        public bool Connected
        {
            get
            {
                return bdtpClient.Connected;
            }
        }

        public IPAddress RemoteIP
        {
            get
            {
                return remoteIP;
            }
            set
            {
                remoteIP = value;
                OnPropertyChanged("RemoteIP");
            }
        }
        private IPAddress remoteIP;

        public IPAddress LocalIP
        {
            get
            {
                return bdtpClient.LocalIP;
            }
        }

        // Текущее состояние
        public States State
        {
            get
            {
                return state;
            }
            set
            {
                state = value;
                OnPropertyChanged("State");

                ControlMedia(ringtone, States.IncomingCall);
                ControlMedia(dialtone, States.OutgoingCall);
            }
        }
        private States state;

        // Плееры звуков
        private MediaPlayer ringtone;
        private MediaPlayer dialtone;

        // Время звонка
        public TimeSpan CallTime
        {
            get
            {
                return callTime;
            }
            set
            {
                callTime = value;
                OnPropertyChanged("CallTime");
            }
        }
        private TimeSpan callTime;
        private DispatcherTimer callTimer;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string PropertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
        }
        
        public VoiceChatModel()
        {
            bdtpClient = new BdtpClient(GetLocalIP());

            InitializeEvents();
            InitializeAudio();
            InitializeMedia();
            InitializeTimers();

            BeginWaitCall();
        }

        // Инициализация
        private void InitializeEvents()
        {
            bdtpClient.ReceiptReceived += ReceiveAccept;
            bdtpClient.ReceiptReceived += ReceiveDisconnect;
        }
        private void InitializeAudio()
        {
            // Cоздаем поток для записи нашей речи определяем его формат - 
            // частота дискретизации 8000 Гц, ширина сэмпла - 16 бит, 1 канал - моно
            input = new WaveIn();
            input.WaveFormat = new WaveFormat(8000, 16, 1);

            // Создание потока для прослушивания входящиего звука
            output = new WaveOut();
            bufferStream = new BufferedWaveProvider(new WaveFormat(8000, 16, 1));
            output.Init(bufferStream);
        }
        private void InitializeMedia()
        {
            LoadMedia(ref ringtone, "Media/ringtone.mp3");

            LoadMedia(ref dialtone, "Media/dialtone.mp3");
            dialtone.Volume = 0.1;
        }
        private void InitializeTimers()
        {
            callTimer = new DispatcherTimer();
            callTimer.Interval = TimeSpan.FromSeconds(1);
            callTimer.Tick += (sender, e) => CallTime += callTimer.Interval;
        }

        // Работа со свуками
        private void LoadMedia(ref MediaPlayer media, string path)
        {
            media = new MediaPlayer();
            media.Open(new Uri(path, UriKind.Relative));
            media.MediaEnded += Media_Restart;
        }
        private void Media_Restart(object sender, EventArgs e)
        {
            MediaPlayer media = sender as MediaPlayer;
            media.Stop();
            media.Play();
        }
        private void ControlMedia(MediaPlayer media, States state)
        {
            if (media == null)
            {
                return;
            }

            if (State == state)
            {
                media.Dispatcher.Invoke(() => media.Play());
            }
            else
            {
                media.Dispatcher.Invoke(() => media.Stop());
            }
        }

        private IPAddress GetLocalIP()
        {
            IPAddress[] addresses = Dns.GetHostAddresses(Dns.GetHostName());
            return addresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork).Last();
        }

        // Обработчики приема подтверждений
        private void ReceiveAccept(byte[] buffer)
        {
            if (buffer.Length == 1 && buffer[0] == (byte)Receipts.Accept)
            {
                State = States.Talk;
            }
        }
        private void ReceiveDisconnect(byte[] buffer)
        {
            if (buffer.Length == 0)
            {
                EndCall();
            }
        }

        // Исходящий вызов
        public void BeginCall()
        {
            State = States.OutgoingCall;
            EndWaitCall();

            // Подключение и ожидание ответа
            if (bdtpClient.Connect(remoteIP) && WaitAccept())
            {
                BeginTalk();
            }
            else
            {
                EndCall();
            }
        }
        private bool WaitAccept()
        {
            while (bdtpClient.Connected && State != States.Talk) ;
            
            if (State == States.Talk)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
        public void EndCall()
        {
            if (State == States.Talk)
            {
                EndTalk();
            }

            bdtpClient.Disconnect();

            BeginWaitCall();
        }

        // Входящий вызов
        public void AcceptCall()
        {
            if (bdtpClient.SendReceipt(new byte[] { (byte)Receipts.Accept }))
            {
                BeginTalk();
            }
        }
        public void DeclineCall()
        {
            bdtpClient.Disconnect();

            BeginWaitCall();
        }

        // Ожидание входящего вызова
        private void BeginWaitCall()
        {
            if (State == States.Close)
            {
                return;
            }

            Thread.Sleep(100);

            waitCall = new Thread(WaitCall);
            waitCall.Start();
        }
        private void EndWaitCall()
        {
            bdtpClient.StopAccept();
            waitCall?.Abort();
        }
        private void WaitCall()
        {
            State = States.WaitCall;

            if (bdtpClient.Accept())
            {
                RemoteIP = bdtpClient.RemoteIP;
                
                State = States.IncomingCall;
            }

            EndWaitCall();
        }

        // Разговор
        private void BeginTalk()
        {
            State = States.Talk;

            callTime = new TimeSpan(0);
            callTimer.Start();

            // Передача звука
            input.DataAvailable += SendVoice;
            input.StartRecording();

            // Принятие звука
            output.Play();
            receiveVoice = new Thread(ReceiveVoice);
            receiveVoice.Start();
        }
        private void EndTalk()
        {
            // Передача звука
            input.StopRecording();
            input.DataAvailable -= SendVoice;

            // Принятие звука
            receiveVoice?.Abort();
            output.Stop();

            callTimer.Stop();
            callTime = new TimeSpan(0);
        }

        // Передачи звука
        private void SendVoice(object sender, WaveInEventArgs e)
        {
            if (State != States.Talk)
            {
                return;
            }

            bdtpClient.Send(e.Buffer);
        }
        // Приема звука
        private void ReceiveVoice()
        {
            while(bdtpClient.Connected)
            {
                byte[] data = bdtpClient.Receive();
                bufferStream.AddSamples(data, 0, data.Length);
                Thread.Sleep(0);
            }
        }

        // Закрытие модели
        public void Closing()
        {
            State = States.Close;
            bdtpClient.Disconnect();
            EndWaitCall();
        }
    }
}
