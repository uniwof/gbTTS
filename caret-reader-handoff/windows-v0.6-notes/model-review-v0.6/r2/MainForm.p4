            var clauses = await Task.Run(() => TextSplitter.Split(more, pauses, firstChunk: false, CaretTextGrabber.MaxChars));
            queue.Add(clauses);
            Log.Write("<zh>");
        }
        catch (Exception ex) { Log.Write("<zh>" + ex.Message); }
        finally { queue.Complete(); }
    }
    private void StopReading()
    {
        _readSession++;
        _readCts?.Cancel();
        _readCts = null;
        _sink = null;
        _pauseRequested = false;
        if (_state is State.Reading or State.Grabbing or State.Paused) SetState(State.Idle);
    }
    private async Task OnVoiceChangedAsync()
    {
        if (_voiceMenu.SelectedItem is not VoicePack v) return;
        _settings.VoiceId = v.Id;
        try { _settings.Save(_settingsPath); } catch { }
        StopReading();
        await LoadSelectedVoiceAsync();
    }
    private async Task LoadSelectedVoiceAsync(bool forceReload = false)
    {
        if (_voiceMenu.SelectedItem is not VoicePack voice) return;
        if (!forceReload && _engine.IsLoaded(voice, _settings.NumThreads))
        {
            Log.Write("<zh>");
            SetState(State.Idle);
            FlashStatus("<zh>", 2500);
            return;
        }
        if (!_readTask.IsCompleted) { _progressText = "<zh>"; SetState(State.Loading); }
        try { await _readTask; } catch { }
        _progressText = "<zh>";
        SetState(State.Loading);
        CrashGuard.Mark("<zh>");
        _loadExpectSeconds = Math.Max(1, voice.ApproxLoadSeconds);
        _loadClock.Restart();
        _progress = 0;
        _progressTimer.Start();
        try
        {
            int threads = _settings.NumThreads;
            await Task.Run(() =>
            {
                if (forceReload) _engine.Unload();
                _engine.Load(voice, threads, _appDir);
            });
            CrashGuard.Mark("<zh>");
            Log.Write("<zh>");
            _progressTimer.Stop();
            _progress = 1; Redraw();
            await Task.Delay(300);
            _progress = null;
            _bubble.HideNow();
            SetState(State.Idle);
        }
        catch (Exception ex)
        {
            _progressTimer.Stop();
            _progress = null;
            _bubble.HideNow();
            Log.Write("<zh>" + ex);
            SetState(State.NoModel);
            ShowError(FriendlyLoadError(ex));
        }
    }
    private static string FriendlyLoadError(Exception ex)
    {
        var e = ex;
        while (e is TypeInitializationException or TargetInvocationException or AggregateException && e.InnerException != null)
            e = e.InnerException!;
        return e switch
        {
            DllNotFoundException =>
                "<zh>" +
                "<zh>",
            BadImageFormatException => "<zh>",
            EntryPointNotFoundException => "<zh>",
            OutOfMemoryException => "<zh>",
            _ => e.Message,
        };
    }
    private void UpdateLoadProgress()
    {
        double t = _loadClock.Elapsed.TotalSeconds;
        double p = Math.Min(0.95, 1 - Math.Exp(-t / (_loadExpectSeconds * 0.6)));
        _progress = p;
        if (_voiceMenu.SelectedItem is VoicePack v) _progressText = "<zh>";
        Redraw();
        if (_hover || _bubble.Showing) { _bubbleIsHover = false; _bubble.ShowNear(Bounds, _progressText ?? "", 0); }
    }
    private void RefreshVoices(bool tidy)
    {
        VoiceLibrary.ScanResult scan;
        try { scan = VoiceLibrary.Scan(_appDir, tidy); }
        catch (Exception ex) { Log.Write("<zh>" + ex); return; }
        string problems = string.Join("\n", scan.Problems);
        if (problems != _lastScanProblems)
        {
            foreach (var pr in scan.Problems) Log.Write("<zh>" + pr);
            _lastScanProblems = problems;
        }
        var current = _voiceMenu.SelectedItem as VoicePack;
        string? wantId = current?.Id ?? _settings.VoiceId;
        var list = scan.Packs;
        if (current != null && !list.Any(p => p.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase)) && _engine.LoadedPackId == current.Id)
            list.Add(current);
        bool same = list.Count == _packs.Count && list.Zip(_packs).All(t => t.First.Id == t.Second.Id && t.First.Name == t.Second.Name
                                                                        && t.First.SamplePath == t.Second.SamplePath);
        if (same && !tidy) return;
        _packs = list;
        _voiceMenu.SetItems(_packs);
        int idx = wantId == null ? -1 : _packs.FindIndex(p => p.Id.Equals(wantId, StringComparison.OrdinalIgnoreCase));
        _voiceMenu.SelectedIndex = idx >= 0 ? idx : (_packs.Count > 0 ? 0 : -1);
        Log.Write("<zh>"，"<zh>");
    }
    private void PreviewVoice(int index)
    {
        if (index < 0 || index >= _packs.Count) return;
        var p = _packs[index];
        if (p.SamplePath == null) { FlashStatus("<zh>"); return; }
        if (_player.PlayingId != p.Id && _state == State.Reading) PauseReading();
        Log.Write("<zh>");
        _player.Toggle(p.Id, p.SamplePath);
    }
    private async void ImportVoices()
    {
        if (_importing || _state == State.Loading) return;
        StopReading();
        _player.Stop();
        string? folder;
        using (var dlg = new FolderBrowserDialog
        {
            Description = "<zh>",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        })
        {
            var r = dlg.ShowDialog(this);
            folder = r == DialogResult.OK ? dlg.SelectedPath : null;
        }
        ReturnFocus();
        if (string.IsNullOrEmpty(folder)) return;
        var before = _state;
        _importing = true;
        _progress = 0;
        FlashStatus("<zh>", 0);
        var progress = new Progress<(string text, double frac)>(x =>
        {
            if (!_importing || IsDisposed) return;
            _progress = x.frac;
            Redraw();
            _bubble.ShowNear(Bounds, $"{x.text} {(int)(x.frac * 100)}%", 0);
        });
        IProgress<(string, double)> rep = progress;
        VoiceImporter.Result result;
        try
        {
            Log.Write("<zh>" + folder);
            result = await Task.Run(() => VoiceImporter.Import(folder, _appDir, (t, f) => rep.Report((t, f)), CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log.Write("<zh>" + ex);
            result = new VoiceImporter.Result(new() { new VoiceImporter.Item(folder, null, null, ex.Message) });
        }
        finally
        {
            _importing = false;
            _progress = null;
            _bubble.HideNow();
            Redraw();
        }
        RefreshVoices(tidy: false);
        SetState(before == State.NoModel && _engine.LoadedModelId != null ? State.Idle : before);
        var ok = result.Imported.ToList();
        var skipped = result.Items.Where(i => i.Skipped).ToList();
        var bad = result.Items.Where(i => i.Error != null).ToList();
        var msg = new List<string>();
        if (ok.Count > 0) msg.Add("<zh>" + string.Join("\n  ", ok.Select(i => i.Name)) + "<zh>");
        if (skipped.Count > 0) msg.Add("<zh>" + string.Join("\n  ", skipped.Select(i => i.Name)));
        if (bad.Count > 0) msg.Add("<zh>" + string.Join("\n  ", bad.Select(i => (i.Name != null ? i.Name + "：" : "") + i.Error)));
        if (msg.Count > 0) ShowError(string.Join("\n\n", msg), info: bad.Count == 0);
        if (before == State.NoModel && _engine.LoadedModelId == null && _packs.Count > 0)
        {
            var first = ok.Select(i => _packs.FindIndex(p => p.Id == i.Id)).FirstOrDefault(i => i >= 0, 0);
            _voiceMenu.SelectedIndex = first;
            await OnVoiceChangedAsync();
        }
    }
    private async void OpenSettings()
    {
        StopReading();
        _player.Stop();
        UnregisterHotkeys();
        using var dlg = new SettingsForm(_settings.Clone(), _settingsPath);
        var result = dlg.ShowDialog(this);
        ReturnFocus();
        if (result != DialogResult.OK)
        {
            ApplyHotkeys(_settings.Hotkey, _settings.StopHotkey, showErrors: true);
            return;
        }
        var n = dlg.Result;
        bool threadsChanged = n.NumThreads != _settings.NumThreads;
        n.WindowX = _settings.WindowX; n.WindowY = _settings.WindowY; n.VoiceId = _settings.VoiceId;
        n.BallX = Left; n.BallY = Top; n.Appearance = _settings.Appearance;
        _settings = n;
        TopMost = _settings.TopMost;
        if (!_settings.TopMost) Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        else ReassertTopMost();
        ApplyHotkeys(_settings.Hotkey, _settings.StopHotkey, showErrors: true);
        _settings.Hotkey = _hotkey.ToString();
        _settings.StopHotkey = _stopHotkey.ToString();
        try { _settings.Save(_settingsPath); }
        catch (Exception ex) { ShowError("<zh>" + ex.Message); }
        Log.Write("<zh>");
        if (threadsChanged && _state != State.NoModel)
        {
            StopReading();
            await LoadSelectedVoiceAsync(forceReload: true);
        }
    }
    private void SetState(State s)
    {
        var old = _state;
        _state = s;
        if (s != State.Reading) _mouthLevel = 0;
        if (old != s) { _petFrame = s == State.Reading ? "talk0" : "idle"; _idleSeq = 0; }