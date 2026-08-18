global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;

// CutFlow is a WPF application. Keep these aliases explicit so adding the
// WinForms tray-icon reference can never make editor types ambiguous again.
global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Color = System.Windows.Media.Color;
global using ComboBox = System.Windows.Controls.ComboBox;
global using DragEventArgs = System.Windows.DragEventArgs;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using Pen = System.Windows.Media.Pen;
global using Point = System.Windows.Point;
global using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
global using Size = System.Windows.Size;
