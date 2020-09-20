' Copyrigth Rob Latour, 2020

Imports System.Diagnostics
Imports System.Threading
Imports System.Timers
Imports System
Imports System.Management
Imports System.ComponentModel
Imports System.Runtime.Remoting.Messaging
Imports System.Runtime.CompilerServices

' will autostart after install ref: https://www.aspsnippets.com/Articles/Start-Windows-Service-Automatically-start-after-Installation-in-C-and-VBNet.aspx#:~:text=We%20can%20start%20the%20Windows,to%20start%20the%20Windows%20Service.

Public Class CPUMonitorJr

    '  the frequency at which data is sent to the esp32 is controlled in the setting variable
    '  My.Settings.Interval
    '  the value represents the number of milli seconds between update
    '  for example, 1000 means 1 second between updates
    '  default value is 1000, an number below 200  (1/5th of a second) doesn't work out well as it drives too much overhead
    '  therefore the minimal value you can set is 100; however a value of 1000 seems to give good results

    Private Const gUDPListenPort As Integer = 44445

    Private Const DebugOn As Boolean = False ' manually create the folder "c:\temp" before running this service with DebugOn = True
    Private Const DebugFilename As String = "C:\temp\CPUMonitorJr_debug.txt"

    Private gDefaultESP32Address As String = ""

    Private cpu As New PerformanceCounter()
    Private WebSocket As WebSocket4Net.WebSocket
    Private WorkingInterval As Integer

    Private SendTheTime As Boolean = True

    Friend WithEvents Timer As System.Timers.Timer = New System.Timers.Timer()

    Protected Overrides Sub OnStart(ByVal args() As String)

        Try

            If DebugOn Then

                My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Starting" & vbCrLf, False)

            End If

            Try

                Broadcast.UDP_Listen1(44445)
                AddHandler Broadcast.DataArrival1, AddressOf UDPTrigger
                If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Listening on UDP port " & gUDPListenPort.ToString & vbCrLf, True)

            Catch ex As Exception
                If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Can not listen on port" & vbCrLf & ex.Message.ToString & vbCrLf, True)
            End Try

            gDefaultESP32Address = My.Settings.ESP32IPAddress

            OpenSocket()

            Try

                WorkingInterval = CType(My.Settings.Interval, Integer)
                If WorkingInterval < 200 Then WorkingInterval = 200

            Catch ex As Exception

                WorkingInterval = 1000

            End Try

            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Interval = " & WorkingInterval & vbCrLf, True)

            SendTime()
            Thread.Sleep(1000)

            Timer.Enabled = True
            Timer.Interval = WorkingInterval
            AddHandler Timer.Elapsed, AddressOf Timer_Elapsed
            Timer.Start()

            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Startup complete" & vbCrLf, True)

        Catch ex As Exception

            My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & ex.ToString & vbCrLf, True)

        End Try

    End Sub

    Protected Overrides Sub OnStop()

        If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Closing UDP listening port" & vbCrLf, True)
        modBroadcast.Broadcast.CloseSock1()

    End Sub

    Sub socketOpened(ByVal s As Object, ByVal e As EventArgs)

    End Sub

    Sub socketClosed(ByVal s As Object, ByVal e As EventArgs)

    End Sub

    Sub socketError(ByVal s As Object, ByVal e As SuperSocket.ClientEngine.ErrorEventArgs)

    End Sub

    Sub socketMessage(ByVal s As Object, ByVal e As WebSocket4Net.MessageReceivedEventArgs)

    End Sub
    Sub socketDataReceived(ByVal ss As Object, ByVal e As WebSocket4Net.DataReceivedEventArgs)

    End Sub

    Private Sub Timer_Elapsed(sender As Object, e As ElapsedEventArgs) Handles Timer.Elapsed

        If SendTheTime Then
            SendTime()
            SendTheTime = False
        Else
            SendTempAndCPUReadings()
        End If

    End Sub

    Private Sub SendTempAndCPUReadings()

        Try

            ' get the temperature 

            Dim temperature As Single = GetTemp()

            ' send the temperature 
            Dim tempArray(1) As Byte  'byte 0 will be used for the whole number part of the temperture, byte 1 will be used for the decimal part of the tempeture

            Dim wholeNumber As Integer = Math.Truncate(temperature)
            tempArray(0) = wholeNumber
            tempArray(1) = (Math.Truncate(100 * temperature)) - wholeNumber * 100

            ' get the cpu readings 

            Dim pc As PerformanceCounter = New PerformanceCounter("Processor Information", "% Processor Time")
            Dim cat = New PerformanceCounterCategory("Processor Information")
            Dim instanceNames As String() = cat.GetInstanceNames

            Dim filteredInstanceNames(instanceNames.Length) As String
            Dim filteredInstancesIndex As Integer = 0

            ' filter out totals
            For Each thing In instanceNames
                If thing.EndsWith("Total") Then
                Else
                    filteredInstanceNames(filteredInstancesIndex) = thing
                    filteredInstancesIndex += 1
                End If
            Next

            ReDim Preserve filteredInstanceNames(filteredInstancesIndex - 1)
            Array.Sort(filteredInstanceNames)

            Dim cs = New Dictionary(Of String, CounterSample)
            For Each s In filteredInstanceNames
                pc.InstanceName = s
                cs.Add(s, pc.NextSample)
            Next

            Dim numberOfCores As Integer = filteredInstanceNames.Count
            Dim CPUValues(numberOfCores - 1) As Double
            Dim index As Integer = 0

            Thread.Sleep(50)  ' this is absolutely needed, do not remove ( learned that the hard way over a several days! ) 

            For Each s In filteredInstanceNames

                pc.InstanceName = s
                CPUValues(index) = calculateCPUValue(cs(s), pc.NextSample)
                cs(s) = pc.NextSample

                index += 1

            Next

            ' send the CPU readings

            ' send data

            ' byte 0 = is a 1 to represent this is a temperature and cpu data stream 
            ' byte 1 = temp whole number
            ' byte 2 = temp decimal
            ' byte 3 = number of cpus
            ' byte 4 and on  = cpu busy of each cpu

            Dim sendArray(numberOfCores + 3) As Byte

            sendArray(0) = 1
            sendArray(1) = tempArray(0)
            sendArray(2) = tempArray(1)
            sendArray(3) = numberOfCores

            For x = 0 To numberOfCores - 1

                If CPUValues(x) > 0 Then
                    sendArray(4 + x) = Int(Math.Round(CPUValues(x)))
                Else
                    sendArray(4 + x) = 0
                End If

            Next

            WebSocket.Send(sendArray, 0, sendArray.Length)

        Catch ex As Exception
            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & ex.ToString & vbCrLf, True)
        End Try

    End Sub

    Public Shared Function calculateCPUValue(ByVal oldSample As CounterSample, ByVal newSample As CounterSample) As Double

        Dim difference As Double = (newSample.RawValue - oldSample.RawValue)
        Dim timeInterval As Double = (newSample.TimeStamp100nSec - oldSample.TimeStamp100nSec)
        If (timeInterval <> 0) Then
            Return (100 * (1 - (difference / timeInterval)))
        End If

        Return 0

    End Function

    Friend Function GetTemp() As Single

        Dim avgTempKelvin As Long = 0
        Dim count As Integer = 0

        Try

            Dim searcher As New ManagementObjectSearcher(
                    "root\WMI",
                    "SELECT * FROM MSAcpi_ThermalZoneTemperature")

            For Each queryObj As ManagementObject In searcher.Get()

                count = count + 1
                avgTempKelvin = avgTempKelvin + queryObj("CurrentTemperature")

            Next

        Catch err As ManagementException

        End Try

        Dim Result As Single = avgTempKelvin / count

        ' Result = Result / 10 - 273.15 ' convert from Kalvin to Celcius
        Result = ((Result / 10) - 273.15) * 9 / 5 + 32 ' convert from Kalvin to Fahrenheit 

        Return Result

    End Function

    Private Sub SendTime()

        ' byte 0 = is a 0 to represent this is a time stream 
        ' byte 1 = year  (current year - 2020) ' warning this will need to change for the year 2275 :-)
        ' byte 2 = month
        ' byte 3 = day
        ' byte 4 = dayofweek
        ' byte 5 = hour
        ' byte 6 = minute
        ' byte 7 = second

        Try

            Dim sendArray(9) As Byte

            Dim RightNow As Date = Now()

            sendArray(0) = 0
            sendArray(1) = RightNow.Year - 2000
            sendArray(2) = RightNow.Month
            sendArray(3) = RightNow.Day
            sendArray(4) = RightNow.DayOfWeek
            sendArray(5) = RightNow.Hour
            sendArray(6) = RightNow.Minute
            sendArray(7) = RightNow.Second

            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "sending time" & vbCrLf, True)

            WebSocket.Send(sendArray, 0, sendArray.Length)

        Catch ex As Exception
            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & ex.ToString & vbCrLf, True)
        End Try

    End Sub

    Friend Sub UDPTrigger(ByVal IncomingData As String) ' added in 3.4.8 to ensure this gets called without interuption

        ' If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & IncomingData & vbCrLf, True)

        Static LastMessageReceived As String = String.Empty
        Static LastMessageReceivedTimeStamp As DateTime = Now.AddDays(-1)

        'ignor duplicate messages within five seconds of each other
        If LastMessageReceived = IncomingData Then

            If LastMessageReceivedTimeStamp.AddSeconds(5) > Now Then

                LastMessageReceivedTimeStamp = Now
                Exit Sub

            End If

        Else

            LastMessageReceived = IncomingData

        End If

        LastMessageReceivedTimeStamp = Now

        If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Incoming udp broadcast" & vbCrLf & IncomingData & vbCrLf, True)

        If IncomingData.StartsWith("CPUMonitorJr") Then

            Dim ws() As String = IncomingData.Split(";")
            gDefaultESP32Address = ws(1).Trim

            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "ESP32 address set to " & gDefaultESP32Address & vbCrLf, True)

            CloseSocket()
            OpenSocket()

            SendTheTime = True

        End If

    End Sub

    Friend Sub OpenSocket()

        Try

            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Opening websocket" & vbCrLf, True)

            With cpu
                .CategoryName = "Processor"
                .CounterName = "% Processor Time"
                .InstanceName = "_Total"
            End With

            Dim dummy As Single = cpu.NextValue()

            Dim WebSocketAddress As String = "ws://" & gDefaultESP32Address & "/cpumonitorjr"

            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "WebSocket Address = " & WebSocketAddress & vbCrLf, True)

            WebSocket = New WebSocket4Net.WebSocket(WebSocketAddress)

            AddHandler WebSocket.Opened, Sub(s, e) socketOpened(s, e)
            AddHandler WebSocket.Error, Sub(s, e) socketError(s, e)
            AddHandler WebSocket.Closed, Sub(s, e) socketClosed(s, e)
            AddHandler WebSocket.MessageReceived, Sub(s, e) socketMessage(s, e)
            AddHandler WebSocket.DataReceived, Sub(s, e) socketDataReceived(s, e)

            WebSocket.Open()

            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket open" & vbCrLf, True)

        Catch ex As Exception

            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket open failed" & vbCrLf & ex.Message.ToString & vbCrLf, True)

        End Try


    End Sub

    Friend Sub CloseSocket()

        If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Closing websocket" & vbCrLf, True)

        Try

            RemoveHandler WebSocket.Opened, Sub(s, e) socketOpened(s, e)
            RemoveHandler WebSocket.Error, Sub(s, e) socketError(s, e)
            RemoveHandler WebSocket.Closed, Sub(s, e) socketClosed(s, e)
            RemoveHandler WebSocket.MessageReceived, Sub(s, e) socketMessage(s, e)
            RemoveHandler WebSocket.DataReceived, Sub(s, e) socketDataReceived(s, e)

            WebSocket.Close()

            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket closed" & vbCrLf, True)

        Catch ex As Exception

            If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket close failed" & vbCrLf & ex.Message.ToString & vbCrLf, True)

        End Try

    End Sub

End Class
