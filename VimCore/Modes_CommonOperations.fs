﻿#light

namespace Vim.Modes
open Vim
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Operations

[<AbstractClass>]
type internal CommonOperations 
    (
        _textView : ITextView,
        _operations : IEditorOperations,
        _host : IVimHost,
        _jumpList : IJumpList,
        _settings : IVimLocalSettings ) =

    member private x.NavigateToPoint (point:VirtualSnapshotPoint) = 
        let buf = point.Position.Snapshot.TextBuffer
        if buf = _textView.TextBuffer then 
            ViewUtil.MoveCaretToPoint _textView point.Position |> ignore
            true
        else  _host.NavigateTo point 

    member private x.DeleteSpan (span:SnapshotSpan) motionKind opKind (reg:Register) =
        let tss = span.Snapshot
        let regValue = {Value=span.GetText();MotionKind=motionKind;OperationKind=opKind}
        reg.UpdateValue(regValue) 
        tss.TextBuffer.Delete(span.Span)

    member x.ShiftSpanRight (span:SnapshotSpan) = 
        let text = new System.String(' ', _settings.GlobalSettings.ShiftWidth)
        let buf = span.Snapshot.TextBuffer
        let startLineNumber = span.Start.GetContainingLine().LineNumber
        let endLineNumber = span.End.GetContainingLine().LineNumber
        use edit = buf.CreateEdit()
        for i = startLineNumber to endLineNumber do
            let line = span.Snapshot.GetLineFromLineNumber(i)
            edit.Replace(line.Start.Position,0,text) |> ignore
        
        edit.Apply() |> ignore
        
    member x.ShiftSpanLeft (span:SnapshotSpan) =
        let count = _settings.GlobalSettings.ShiftWidth
        let fixText (text:string) = 
            let count = min count (text.Length) // Deal with count being greater than line length
            let count = 
                match text |> Seq.tryFindIndex (fun x -> x <> ' ') with
                    | Some(i) ->
                        if i < count then i
                        else count
                    | None -> count
            text.Substring(count)                 
        let buf = span.Snapshot.TextBuffer
        let startLineNumber = span.Start.GetContainingLine().LineNumber
        let endLineNumber = span.End.GetContainingLine().LineNumber
        use edit = buf.CreateEdit()
        for i = startLineNumber to endLineNumber do
            let line = span.Snapshot.GetLineFromLineNumber(i)
            let text = fixText (line.GetText())
            edit.Replace(line.Extent.Span, text) |> ignore
        edit.Apply() |> ignore

    interface ICommonOperations with
        member x.TextView = _textView 
        member x.EditorOperations = _operations

        member x.Join (start:SnapshotPoint) (kind:JoinKind) count = 
    
            // Always joining at least 2 lines so we subtract to get the number of join
            // operations.  1 is a valid input though
            let count = if count > 1 then count-1 else 1
    
            // Join the line returning place the caret should be positioned
            let joinLine (buffer:ITextBuffer) lineNumber =
                let tss = buffer.CurrentSnapshot
                use edit = buffer.CreateEdit()
                let line = buffer.CurrentSnapshot.GetLineFromLineNumber(lineNumber)
                let lineBreakSpan = Span(line.End.Position, line.LineBreakLength)
    
                // Strip out the whitespace at the start of the next line
                let maybeStripWhiteSpace () = 
                    let nextLine = tss.GetLineFromLineNumber(lineNumber+1)
                    let rec countSpace (index) =
                        if index < nextLine.Length && System.Char.IsWhiteSpace(nextLine.Start.Add(index).GetChar()) then
                            countSpace(index+1)
                        else
                            index
                    match countSpace 0 with
                    | 0 -> ()
                    | value -> edit.Delete(nextLine.Start.Position, value) |> ignore
    
                match kind with 
                | RemoveEmptySpaces ->  
                    edit.Replace(lineBreakSpan, " ") |> ignore
                    maybeStripWhiteSpace()
                | KeepEmptySpaces -> 
                    edit.Delete(lineBreakSpan) |> ignore
    
                edit.Apply() |> ignore
                line.End.Position + 1
    
            let joinLineAndMoveCaret lineNumber =
                let caret = joinLine _textView.TextBuffer lineNumber
                ViewUtil.MoveCaretToPosition _textView caret |> ignore
    
            let rec inner count = 
                let tss = _textView.TextBuffer.CurrentSnapshot
                let lineNumber = start.GetContainingLine().LineNumber
                if lineNumber = tss.LineCount + 1 then
                    false
                else
                    joinLineAndMoveCaret lineNumber
                    match count with
                    | 1 -> true
                    | _ -> inner (count-1)
            inner count
                
        member x.GoToDefinition () = 
            let before = ViewUtil.GetCaretPoint _textView
            if _host.GoToDefinition() then
                _jumpList.Add before |> ignore
                Succeeded
            else
                match TssUtil.FindCurrentFullWordSpan _textView.Caret.Position.BufferPosition Vim.WordKind.BigWord with
                | Some(span) -> 
                    let msg = Resources.Common_GotoDefFailed (span.GetText())
                    Failed(msg)
                | None ->  Failed(Resources.Common_GotoDefNoWordUnderCursor) 

        member x.GoToMatch () = _host.GoToMatch()
                
        member x.SetMark (vimBuffer:IVimBuffer) point c = 
            if System.Char.IsLetter(c) || c = '\'' || c = '`' then
                let map = vimBuffer.MarkMap
                map.SetMark point c
                Succeeded
            else
                Failed(Resources.Common_MarkInvalid)

        member x.NavigateToPoint point = x.NavigateToPoint point
                
        member x.JumpToMark ident (map:IMarkMap) = 
            let before = ViewUtil.GetCaretPoint _textView
            let jumpLocal (point:VirtualSnapshotPoint) = 
                ViewUtil.MoveCaretToPoint _textView point.Position |> ignore
                _jumpList.Add before |> ignore
                Succeeded
            if not (map.IsLocalMark ident) then 
                match map.GetGlobalMark ident with
                | None -> Failed Resources.Common_MarkNotSet
                | Some(point) -> 
                    match x.NavigateToPoint point with
                    | true -> 
                        _jumpList.Add before |> ignore
                        Succeeded
                    | false -> Failed Resources.Common_MarkInvalid
            else 
                match map.GetLocalMark _textView.TextBuffer ident with
                | Some(point) -> jumpLocal point
                | None -> Failed Resources.Common_MarkNotSet
    
        member x.YankText text motion operation (reg:Register) =
            let regValue = {Value=text;MotionKind = motion; OperationKind = operation};
            reg.UpdateValue (regValue)

        member x.Yank (span:SnapshotSpan) motion operation (reg:Register) =
            let regValue = {Value=span.GetText();MotionKind = motion; OperationKind = operation};
            reg.UpdateValue (regValue)
        
        member x.PasteAfter (point:SnapshotPoint) text opKind = 
            let buffer = point.Snapshot.TextBuffer
            let replaceSpan = 
                match opKind with
                | OperationKind.LineWise ->
                    let line = point.GetContainingLine()
                    new SnapshotSpan(line.EndIncludingLineBreak, 0)
                | OperationKind.CharacterWise ->
                    let line = point.GetContainingLine()
                    let point =  if point.Position < line.End.Position then point.Add(1) else point
                    new SnapshotSpan(point,0)
                | _ -> failwith "Invalid Enum Value"
            let tss = buffer.Replace(replaceSpan.Span, text)
            new SnapshotSpan(tss, replaceSpan.End.Position, text.Length)
        
        member x.PasteBefore (point:SnapshotPoint) text opKind =
            let buffer = point.Snapshot.TextBuffer
            let span = 
                match opKind with
                | OperationKind.LineWise ->
                    let line = point.GetContainingLine()
                    new SnapshotSpan(line.Start, 0)
                | OperationKind.CharacterWise ->
                    new SnapshotSpan(point,0)
                | _ -> failwith "Invalid Enum Value"
            let tss = buffer.Replace(span.Span, text) 
            new SnapshotSpan(tss,span.End.Position, text.Length)
    
        /// Move the cursor count spaces left
        member x.MoveCaretLeft count = 
            _operations.ResetSelection()
            let caret = ViewUtil.GetCaretPoint _textView
            let span = MotionUtil.CharLeft caret count
            ViewUtil.MoveCaretToPoint _textView span.Start |> ignore
    
        /// Move the cursor count spaces to the right
        member x.MoveCaretRight count =
            _operations.ResetSelection()
            let caret = ViewUtil.GetCaretPoint _textView
            let span = MotionUtil.CharRight caret count
            ViewUtil.MoveCaretToPoint _textView span.End  |> ignore
    
        /// Move the cursor count spaces up 
        member x.MoveCaretUp count =
            _operations.ResetSelection()
            let caret = ViewUtil.GetCaretPoint _textView
            let current = caret.GetContainingLine()
            let count = 
                if current.LineNumber - count > 0 then count
                else current.LineNumber 
            for i = 1 to count do   
                _operations.MoveLineUp(false)
            
        /// Move the cursor count spaces down
        member x.MoveCaretDown count =
            _operations.ResetSelection()
            let caret = ViewUtil.GetCaretPoint _textView
            let line = caret.GetContainingLine()
            let tss = line.Snapshot
            let count = 
                if line.LineNumber + count < tss.LineCount then count
                else (tss.LineCount - line.LineNumber) - 1 
            for i = 1 to count do
                _operations.MoveLineDown(false)

        member x.MoveCaretDownToFirstNonWhitespaceCharacter count = 
            let caret = ViewUtil.GetCaretPoint _textView
            let line = caret.GetContainingLine()
            let line = TssUtil.GetValidLineOrLast caret.Snapshot (line.LineNumber + count)
            let point = TssUtil.FindFirstNonWhitespaceCharacter line
            _operations.ResetSelection()
            ViewUtil.MoveCaretToPoint _textView point |> ignore

        member x.MoveWordForward kind count = 
            let rec inner pos count = 
                if count = 0 then pos
                else 
                    let nextPos = TssUtil.FindNextWordPosition pos kind
                    inner nextPos (count-1)
            let pos = inner (ViewUtil.GetCaretPoint _textView) count
            ViewUtil.MoveCaretToPoint _textView pos |> ignore
            
        member x.MoveWordBackward kind count = 
            let rec inner pos count =
                if count = 0 then pos
                else 
                    let prevPos = TssUtil.FindPreviousWordPosition pos kind
                    inner prevPos (count-1)
            let pos = inner (ViewUtil.GetCaretPoint _textView) count
            ViewUtil.MoveCaretToPoint _textView pos |> ignore

        member x.ShiftSpanRight span = x.ShiftSpanRight span

        member x.ShiftSpanLeft span = x.ShiftSpanLeft span

        member x.ShiftLinesRight count = 
            let point = ViewUtil.GetCaretPoint _textView
            let span = TssUtil.GetLineRangeSpan point count
            x.ShiftSpanRight span

        member x.ShiftLinesLeft count =
            let point = ViewUtil.GetCaretPoint _textView
            let span = TssUtil.GetLineRangeSpan point count
            x.ShiftSpanLeft span
            
        member x.InsertText text count = 
            let text = StringUtil.repeat text count 
            let point = ViewUtil.GetCaretPoint _textView
            use edit = _textView.TextBuffer.CreateEdit()
            edit.Insert(point.Position, text) |> ignore
            edit.Apply()                    

        member x.ScrollLines dir count =
            let lines = _settings.Scroll
            let tss = _textView.TextSnapshot
            let caretPoint = ViewUtil.GetCaretPoint _textView
            let curLine = caretPoint.GetContainingLine().LineNumber
            let newLine = 
                match dir with
                | ScrollDirection.Down -> min (tss.LineCount - 1) (curLine + lines)
                | ScrollDirection.Up -> max (0) (curLine - lines)
                | _ -> failwith "Invalid enum value"
            let newCaret = tss.GetLineFromLineNumber(newLine).Start
            _operations.ResetSelection()
            _textView.Caret.MoveTo(newCaret) |> ignore
            _textView.Caret.EnsureVisible()
    
        member x.ScrollPages dir count = 
            let func =
                match dir with
                | ScrollDirection.Down -> _operations.ScrollPageDown 
                | ScrollDirection.Up -> _operations.ScrollPageUp
                | _ -> failwith "Invalid enum value"
            for i = 1 to count do
                func()

        member x.DeleteSpan span motionKind opKind reg = x.DeleteSpan span motionKind opKind reg
    
        member x.DeleteLines count reg = 
            let point = ViewUtil.GetCaretPoint _textView
            let point = point.GetContainingLine().Start
            let span = TssUtil.GetLineRangeSpan point count
            let span = SnapshotSpan(point, span.End)
            x.DeleteSpan span MotionKind.Inclusive OperationKind.LineWise reg |> ignore

        member x.DeleteLinesFromCursor count reg = 
            let point = ViewUtil.GetCaretPoint _textView
            let span = TssUtil.GetLineRangeSpan point count
            let span = SnapshotSpan(point, span.End)
            x.DeleteSpan span MotionKind.Inclusive OperationKind.CharacterWise reg |> ignore

        member x.DeleteLinesIncludingLineBreak count reg = 
            let point = ViewUtil.GetCaretPoint _textView
            let point = point.GetContainingLine().Start
            let span = TssUtil.GetLineRangeSpanIncludingLineBreak point count
            let span = SnapshotSpan(point, span.End)
            x.DeleteSpan span MotionKind.Inclusive OperationKind.LineWise reg |> ignore

        member x.DeleteLinesIncludingLineBreakFromCursor count reg = 
            let point = ViewUtil.GetCaretPoint _textView
            let span = TssUtil.GetLineRangeSpanIncludingLineBreak point count
            let span = SnapshotSpan(point, span.End)
            x.DeleteSpan span MotionKind.Inclusive OperationKind.CharacterWise reg |> ignore

        member x.Save() = _host.SaveCurrentFile()
        member x.SaveAs fileName = _host.SaveCurrentFileAs fileName
        member x.Close checkDirty = _host.CloseCurrentFile checkDirty
        member x.GoToNextTab count = _host.GoToNextTab count
        member x.GoToPreviousTab count = _host.GoToPreviousTab count
        
