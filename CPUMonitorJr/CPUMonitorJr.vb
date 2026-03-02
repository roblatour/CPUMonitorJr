' Copyright Rob Latour, 2026
Imports System.Runtime.InteropServices

Imports System.Threading
Imports System.Timers
Imports System.Management
Imports System.Net
Imports System.Net.Sockets
Imports System.Runtime.CompilerServices
Imports Microsoft.VisualBasic.Devices
Imports System.Data.Common
Imports System.Runtime.Remoting.Lifetime
Imports System.Reflection
Imports System.ServiceProcess
Imports LibreHardwareMonitor.Hardware
Imports GetCoreTempInfoNET

' will auto-start after install ref: https://www.aspsnippets.com/Articles/Start-Windows-Service-Automatically-start-after-Installation-in-C-and-VBNet.aspx#:~:text=We%20can%20start%20the%20Windows,to%20start%20the%20Windows%20Service.

Public Class CPUMonitorJr

    '  the frequency at which data is sent to the esp32 is controlled in the setting variable My.Settings.Interval
    '  on the computer side this variable can be changed by editing in the file 'CPUMonitorJr.exe.config' which is found in the same directory as the program 'CPUMonitorJr.exe'
    '  changing the value requires the CPUMonitorJr service to be stopped and restarted
    '  the value represents the number of milliseconds between update
    '  for example, 1000 means 1 second between updates
    '  default value is 1000, an number below 200  (1/5th of a second) doesn't work out well as it drives too much overhead
    '  therefore the minimal value you can set is 100; however a value of 1000 seems to give good results

    ' the computer running this program and the ESP32 running the CPUMonitorJr sketch both must be communicating via the same UDP Port
    ' on the computer side this variable can be changed by editing in the file 'CPUMonitorJr.exe.config' which is found in the same directory as the program 'CPUMonitorJr.exe'
    ' changing the value requires the CPUMonitorJr service to be stopped and restarted
    ' on the ESP32 side this variable can be changed in the 'user_settings.h' file - and requires the sketch to be recompiled and uploaded to the ESP32

    Private gUDPListenPort As Integer

    ' the timer controls the sending of information to the ESP32;
    ' it is started and stopped depending on if the web socket is open or not
    ' in this way reporting data is not transmitted over the network unless the ESP32 is connected

    Friend WithEvents Timer As System.Timers.Timer

    ' the following controls debug logging 

    Private Const gDebugOn As Boolean = True ' manually create the folder "c:\temp" before running this service with DebugOn = True
    Private Const gDebugFilename As String = "C:\temp\CPUMonitorJr_debug.txt"

    ' the following is used to get the external IP address of the computer running this service

    Private Const gExternalIPIdentificationService As String = "https://api.ipify.org"

    ' used by LibreHardwareMonitorLib (https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
    ' to get temperature readings through PawnIO (https://github.com/namazso/PawnIO)

    Private Const gPawnIOServiceName As String = "pawnio"
    Private Const gPawnIOHealthCheckIntervalSeconds As Integer = 15
    Private Const gPawnIORestartWindowMinutes As Integer = 5
    Private Const gPawnIOMaxRestartAttemptsInWindow As Integer = 3
    Private Const gPawnIOLogCooldownSeconds As Integer = 60

    Private updateVisitor As LibreHardwareMonitor.UpdateVisitor
    Private computer As Global.LibreHardwareMonitor.Hardware.Computer
    Private gCanUseLibreHardwareMonitor As Boolean = False
    Private gLastPawnIOHealthCheckUtc As DateTime = DateTime.MinValue
    Private gLastPawnIOStatusKnownGood As Boolean = False
    Private gPawnIORestartWindowStartUtc As DateTime = DateTime.MinValue
    Private gPawnIORestartAttemptsInWindow As Integer = 0
    Private gLastPawnIOMissingLogUtc As DateTime = DateTime.MinValue
    Private gLastPawnIORestartFailureLogUtc As DateTime = DateTime.MinValue
    Private gNoTemperatureWarningLogged As Boolean = False
    Private gIsStopping As Boolean = False

    ' used by Core Temp to get temperature readings 

    Friend CTInfo As New CoreTempInfo

    ' used for communication between the computer and the ESP32

    Private gWebSocket As WebSocket4Net.WebSocket
    Private gESP32IPAddress As String = ""       ' the ESP32's IP address will be automatically updated here when the ESP32 connects; it is used to open the web socket

    ' some misc. stuff

    Private Shared gServiceName As String
    Private Shared gServiceVersion As String

    Private Const gMaxCPUCoresSupported = 48

    Private Const gFiveNinths As Single = 5 / 9  ' used to convert Fahrenheit to Celsius

    Private Shared gSendTheTime As Boolean = True

    Private Shared gSendTheComputerNameAndIPAddresses As Boolean = True



    Private sw As New Stopwatch

    Protected Overrides Sub OnStart(ByVal args() As String)

        gIsStopping = False

        Try

            If gDebugOn Then
                Dim executingAssembly As Assembly = Assembly.GetExecutingAssembly()
                gServiceName = executingAssembly.GetName().Name
                gServiceVersion = executingAssembly.GetName().Version.ToString()
                My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Starting up " & gServiceName & " version " & gServiceVersion & vbCrLf, True)
                My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "For more information please see: https://github.com/roblatour/CPUMonitorJr" & vbCrLf, True)
            End If

            Try
                If EnsurePawnIOReadyForStartup() Then
                    updateVisitor = New LibreHardwareMonitor.UpdateVisitor()
                    computer = New Global.LibreHardwareMonitor.Hardware.Computer()
                    computer.IsCpuEnabled = True
                    computer.IsMotherboardEnabled = True
                    computer.IsControllerEnabled = True
                    computer.Open()
                    computer.Accept(updateVisitor)
                    gCanUseLibreHardwareMonitor = True
                Else
                    gCanUseLibreHardwareMonitor = False
                    computer = Nothing
                    If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO unavailable at startup. LibreHardwareMonitor disabled; CoreTemp/WMI fallback will be used." & vbCrLf, True)
                End If
            Catch ex As Exception
                gCanUseLibreHardwareMonitor = False
                computer = Nothing
                If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "LibreHardwareMonitor init failed" & vbCrLf & ex.ToString & vbCrLf, True)
            End Try

            ' establish a timer which will be used to trigger sending data to the ESP32
            ' however, don't start it now; rather it will be started once the ESP32 identifies itself later on

            Timer = New System.Timers.Timer With {
                .Enabled = False
            }
            Dim Interval As Integer = Math.Max(My.Settings.Interval, 200) ' determine how frequently data should be sent to the ESP32, minimum is once every 200 milliseconds
            Timer.Interval = Interval
            AddHandler Timer.Elapsed, AddressOf Timer_Elapsed

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Interval = " & Interval & vbCrLf, True)

            gUDPListenPort = CInt(My.Settings.UDPPort.Trim)

            Try
                ' send a broadcast asking ESP32 to identify itself

                AddHandler Broadcast.DataArrival1, AddressOf UDPTrigger
                Broadcast.UDP_Listen1(gUDPListenPort)
                If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Listening on UDP port " & gUDPListenPort.ToString & vbCrLf, True)

            Catch ex As Exception

                If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Can not listen on port" & vbCrLf & ex.Message.ToString & vbCrLf, True)

            End Try

        Catch ex As Exception

            My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & ex.ToString & vbCrLf, True)

        End Try

        If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Startup complete" & vbCrLf, True)

    End Sub

    Protected Overrides Sub OnStop()

        gIsStopping = True

        If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Closing UDP listening port" & vbCrLf, True)

        Try
            If Timer IsNot Nothing Then Timer.Stop()
        Catch ex As Exception
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Timer stop failed" & vbCrLf & ex.ToString & vbCrLf, True)
        End Try

        Try
            If computer IsNot Nothing Then computer.Close()
        Catch ex As Exception
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Computer close failed" & vbCrLf & ex.ToString & vbCrLf, True)
        End Try

        Try
            modBroadcast.Broadcast.CloseSock1()
        Catch ex As Exception
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "UDP socket close failed" & vbCrLf & ex.ToString & vbCrLf, True)
        End Try

        If gDebugOn Then
            My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & gServiceName & " version " & gServiceVersion & " stopped " & vbCrLf, True)
        End If

    End Sub

    Friend Sub UDPTrigger(ByVal IncomingData As String)

        If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "UDPTrigger: " & IncomingData & vbCrLf, True)

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

        If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Incoming udp broadcast: " & IncomingData & vbCrLf, True)

        If IncomingData.StartsWith("CPUMonitorJr") Then

            Dim ws() As String = IncomingData.Split(";")
            gESP32IPAddress = ws(1).Trim

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "ESP32 address set to " & gESP32IPAddress & vbCrLf, True)

            If gWebSocket IsNot Nothing Then
                If gWebSocket.State <> WebSocket4Net.WebSocketState.Closed Then
                    CloseSocket()
                End If
            End If

            OpenSocket()

        End If

    End Sub

    Friend Sub OpenSocket()

        Try

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Opening websocket" & vbCrLf, True)

            Dim WebSocketAddress As String = "ws://" & gESP32IPAddress & "/cpumonitorjr" & gUDPListenPort

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "WebSocket Address = " & WebSocketAddress & vbCrLf, True)

            gWebSocket = New WebSocket4Net.WebSocket(WebSocketAddress)

            AddHandler gWebSocket.Opened, AddressOf SocketOpened
            AddHandler gWebSocket.Error, AddressOf SocketError
            AddHandler gWebSocket.Closed, AddressOf SocketClosed
            AddHandler gWebSocket.MessageReceived, AddressOf SocketMessage
            AddHandler gWebSocket.DataReceived, AddressOf SocketDataReceived

            gWebSocket.Open()

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket open" & vbCrLf, True)

        Catch ex As Exception

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket open failed" & vbCrLf & ex.Message.ToString & vbCrLf, True)

        End Try


    End Sub

    Friend Sub CloseSocket()

        If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Closing websocket" & vbCrLf, True)

        Try

            RemoveHandler gWebSocket.Opened, AddressOf SocketOpened
            RemoveHandler gWebSocket.Error, AddressOf SocketError
            RemoveHandler gWebSocket.Closed, AddressOf SocketClosed
            RemoveHandler gWebSocket.MessageReceived, AddressOf SocketMessage
            RemoveHandler gWebSocket.DataReceived, AddressOf SocketDataReceived

            gWebSocket.Close()

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket closed" & vbCrLf, True)

        Catch ex As Exception

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Websocket close failed" & vbCrLf & ex.Message.ToString & vbCrLf, True)

        End Try

    End Sub

    Sub SocketOpened(ByVal s As Object, ByVal e As EventArgs)

        ' each time the web socket is opened, send the time, computer name, and IP Addresses
        gSendTheTime = True
        gSendTheComputerNameAndIPAddresses = True
        Timer.Enabled = True
        Timer.Start()

    End Sub

    Sub SocketClosed(ByVal s As Object, ByVal e As EventArgs)

        Timer.Stop()

    End Sub

    Sub SocketError(ByVal s As Object, ByVal e As SuperSocket.ClientEngine.ErrorEventArgs)

    End Sub

    Sub SocketMessage(ByVal s As Object, ByVal e As WebSocket4Net.MessageReceivedEventArgs)

    End Sub
    Sub SocketDataReceived(ByVal ss As Object, ByVal e As WebSocket4Net.DataReceivedEventArgs)

    End Sub

    Private synlockObject As New Object

    Private Sub Timer_Elapsed(sender As Object, e As ElapsedEventArgs) Handles Timer.Elapsed

        If gIsStopping Then Exit Sub

        ' for this timer tick

        '    when the gSendTheTime flag is set send the time data
        '    also send the time data approximately 6 hours after the last time the time data was sent
        '    this will help keep the time displayed on the esp32 in sync with the computer's time

        '    when the gSendTheComputerNameAndIPAddresses flag is set send the computer name and IP address 

        '    if neither the gSendTheTime nor gSendTheComputerNameAndIPAddresses flag are set, send the memory, temperature and CPU readings

        Static Dim TimeSinceTimeWasLastSent = 0
        Const delayAfterSendingTheTimeOrComputerNameAndIPAddresses = 1000
        Const SixHours As Integer = 6 * 60 * 60 * 1000

        sw.Start()

        SyncLock synlockObject

            Try

                Dim SendTheMemoryTemperatureAndCPUReadings As Boolean = Not (gSendTheTime OrElse gSendTheComputerNameAndIPAddresses)

                If gSendTheTime OrElse (TimeSinceTimeWasLastSent > SixHours) Then

                    SendTime()
                    TimeSinceTimeWasLastSent = 0
                    Thread.Sleep(delayAfterSendingTheTimeOrComputerNameAndIPAddresses)

                End If

                If gSendTheComputerNameAndIPAddresses Then

                    SendComputerNameAndIPAddresss()
                    Thread.Sleep(delayAfterSendingTheTimeOrComputerNameAndIPAddresses)

                End If

                If SendTheMemoryTemperatureAndCPUReadings Then
                    SendMemoryTemperatureAndCPUReadings()
                End If

            Catch ex As Exception
                If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & ex.ToString & vbCrLf, True)
            End Try

        End SyncLock

        sw.Stop()

        TimeSinceTimeWasLastSent += Timer.Interval + sw.ElapsedMilliseconds

    End Sub

    Private Sub SendTime()

        ' byte 0 = is a '0' to represent this is a time stream 
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

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Sending time" & vbCrLf, True)

            gWebSocket.Send(sendArray, 0, sendArray.Length)

            gSendTheTime = False

        Catch ex As Exception
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & ex.ToString & vbCrLf, True)
        End Try

    End Sub

    Private Sub SendComputerNameAndIPAddresss()

        ' byte 0                  = is a '1' to represent this is a Computer name and IP address stream
        ' byte 1 ... byte n       = Computer name
        ' byte n + 1              = ';'
        ' byte n + 2 ... byte m   = LAN IP Address 
        ' byte m + 1              = ';'
        ' byte m + 2 ... byte z   = External IP Address 
        ' final byte              = ';'

        Try

            Dim Computer_Name As String = Dns.GetHostName()
            Dim LAN_Address As String = ""
            Dim External_Address As String = ""

            ' get LAN address
            Dim host As IPHostEntry = Dns.GetHostEntry(Computer_Name)
            For Each ip As IPAddress In host.AddressList

                If ip.AddressFamily = AddressFamily.InterNetwork Then
                    LAN_Address = ip.ToString
                    Exit For
                End If

            Next

            ' get External Address
            Try
                Using client As New System.Net.WebClient()
                    External_Address = client.DownloadString(gExternalIPIdentificationService).Trim()
                End Using
            Catch ex As Exception
            End Try

            If (External_Address.Length = 0) Then External_Address = "External address not available"

            Dim ComputerNameAndIPAddresses = Computer_Name & ";" & LAN_Address & ";" & External_Address & ";"

            Dim sendArray() As Byte = System.Text.Encoding.ASCII.GetBytes("_" & ComputerNameAndIPAddresses)

            sendArray(0) = 1
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "Sending: " & ComputerNameAndIPAddresses & vbCrLf, True)

            gWebSocket.Send(sendArray, 0, sendArray.Length)

            gSendTheComputerNameAndIPAddresses = False

        Catch ex As Exception
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & ex.ToString & vbCrLf, True)
        End Try

    End Sub

    Private Sub SendMemoryTemperatureAndCPUReadings()


        ' in the tempArray:
        '    byte 0 will be used for the whole number part of the percent of memory used
        '    byte 1 will be used for the decimal part of the percent of memory used
        '    byte 2 will be used for the whole number part of the average temperature
        '    byte 3 will be used for the decimal part of the average temperature
        '    byte 4 will be used for the whole number part of the max temperature
        '    byte 5 will be used for the decimal part of the max temperature

        Static Dim tempArray(6) As Byte

        Static Dim numberOfCores As Integer = 0

        Try

            ' send data

            ' byte 0 = is a '2' to represent this is a temperature and CPU data stream 
            ' byte 1 = percent of memory used whole number
            ' byte 2 = percent of memory used decimal
            ' byte 3 = average temp whole number
            ' byte 4 = average temp decimal
            ' byte 3 = max temp whole number
            ' byte 5 = max temp decimal
            ' byte 7 = number of CPUs
            ' byte 8 and on  = CPU busy of each CPU

            Dim CPUValues(gMaxCPUCoresSupported - 1) As Double

            Dim wholeNumber, decimalNumber As Integer

            Dim TemperatureToOneDecimalPlace As Single

            ' get the percent of memory used from Windows (its not available via Core Temp)

            Dim PercentOfMemoryUsed As Single = (1 - (My.Computer.Info.AvailablePhysicalMemory / My.Computer.Info.TotalPhysicalMemory)) * 100
            Dim PercentOfMemoryUsedToOneDecimalPlace As Single = Math.Round(PercentOfMemoryUsed, 1, MidpointRounding.AwayFromZero)

            ' load the percent of memory used

            wholeNumber = Math.Truncate(PercentOfMemoryUsedToOneDecimalPlace)
            decimalNumber = Math.Truncate(PercentOfMemoryUsedToOneDecimalPlace * 10) - wholeNumber * 10
            tempArray(0) = wholeNumber
            tempArray(1) = decimalNumber

            ' get the average and max temperatures

            ' the first approach try using the Libre Hardware Monitor approach

            Dim averageTemperature As Single = 0
            Dim maximumTemperature As Single = 0

            If gCanUseLibreHardwareMonitor AndAlso computer IsNot Nothing AndAlso EnsurePawnIOReadyDuringRuntime() Then
                computer.Accept(updateVisitor)

                Dim temperatureReadingCount As Integer = 0
                Dim totalTemperature As Single = 0

                For i As Integer = 0 To computer.Hardware.Count - 1

                    For j As Integer = 0 To computer.Hardware(i).Sensors.Count - 1

                        If computer.Hardware(i).Sensors(j).SensorType = SensorType.Temperature Then

                            Dim includeSensor As Boolean = (computer.Hardware(i).HardwareType = HardwareType.Cpu) OrElse (computer.Hardware(i).Sensors(j).Name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0)
                            If includeSensor Then
                                Dim sensorValue = computer.Hardware(i).Sensors(j).Value
                                If sensorValue.HasValue Then
                                    Dim reading As Single = sensorValue.Value
                                    If reading > 0 Then
                                        temperatureReadingCount += 1
                                        totalTemperature += reading

                                        If reading > maximumTemperature Then
                                            maximumTemperature = reading
                                        End If
                                    End If
                                End If
                            End If

                        End If

                    Next

                    For Each subHardware As IHardware In computer.Hardware(i).SubHardware
                        For k As Integer = 0 To subHardware.Sensors.Count - 1
                            If subHardware.Sensors(k).SensorType = SensorType.Temperature Then
                                Dim includeSubSensor As Boolean = (subHardware.HardwareType = HardwareType.Cpu) OrElse (subHardware.Sensors(k).Name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0)
                                If includeSubSensor Then
                                    Dim subSensorValue = subHardware.Sensors(k).Value
                                    If subSensorValue.HasValue Then
                                        Dim subReading As Single = subSensorValue.Value
                                        If subReading > 0 Then
                                            temperatureReadingCount += 1
                                            totalTemperature += subReading

                                            If subReading > maximumTemperature Then
                                                maximumTemperature = subReading
                                            End If
                                        End If
                                    End If
                                End If
                            End If
                        Next
                    Next

                Next

                If temperatureReadingCount > 0 Then
                    averageTemperature = totalTemperature / temperatureReadingCount
                End If
            End If

            ' if the LibreHardwareMonitorLib (through PawnIO) approach did not work try the Core Temp approach  

            If (averageTemperature = 0) AndAlso (maximumTemperature = 0) Then

                Dim reading As Single
                Dim numberOfReadings As Integer = 0
                Dim totalOfAllReadings As Single = 0
                Dim maxiumReading As Single = 0

                Dim bReadSuccess As Boolean = CTInfo.GetData()

                If bReadSuccess Then

                    Dim CPUCount As UInteger = CTInfo.GetCPUCount
                    Dim CoreCount As UInteger = CTInfo.GetCoreCount

                    For i As UInteger = 0 To CPUCount - 1

                        For j As UInteger = 0 To CPUCount - 1

                            numberOfReadings += 1

                            reading = CTInfo.GetTemp(j + (i * CoreCount))

                            totalOfAllReadings += reading

                            If reading > maxiumReading Then
                                maxiumReading = reading
                            End If

                        Next

                    Next

                    If numberOfReadings > 0 Then

                        If CTInfo.IsFahrenheit Then
                            ' convert to Celcius
                            totalOfAllReadings = (totalOfAllReadings - 32) * gFiveNinths
                            maxiumReading = (maxiumReading - 32) * gFiveNinths
                        End If

                        averageTemperature = totalOfAllReadings / numberOfReadings
                        maximumTemperature = maxiumReading

                    End If

                End If

            End If

            If (averageTemperature = 0) AndAlso (maximumTemperature = 0) Then
                Dim wmiTemperature As Single = GetTemp()
                If wmiTemperature > 0 Then
                    averageTemperature = wmiTemperature
                    maximumTemperature = wmiTemperature
                Else
                    If (Not gNoTemperatureWarningLogged) AndAlso gDebugOn AndAlso (Not gIsStopping) Then
                        My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "No temperature available from LibreHardwareMonitor, CoreTemp, or WMI" & vbCrLf, True)
                        gNoTemperatureWarningLogged = True
                    End If
                End If
            End If

            'load the average temperature

            TemperatureToOneDecimalPlace = Math.Round(averageTemperature, 1, MidpointRounding.AwayFromZero)
            wholeNumber = Math.Truncate(TemperatureToOneDecimalPlace)
            decimalNumber = Math.Truncate(TemperatureToOneDecimalPlace * 10) - wholeNumber * 10
            tempArray(2) = wholeNumber
            tempArray(3) = decimalNumber

            'load the max temperature

            TemperatureToOneDecimalPlace = Math.Round(maximumTemperature, 1, MidpointRounding.AwayFromZero)
            wholeNumber = Math.Truncate(TemperatureToOneDecimalPlace)
            decimalNumber = Math.Truncate(TemperatureToOneDecimalPlace * 10) - wholeNumber * 10
            tempArray(4) = wholeNumber
            tempArray(5) = decimalNumber

            ' get the CPU readings 
            ' Windows performance counters are used instead of the results also available from LibreHardwareMonitorLib and Core Temp as Windows performance counters report virtual CPUs where LibreHardwareMonitorLib and Core Temp only report physical CPUs

            Dim pc As New PerformanceCounter("Processor Information", "% Processor Time")
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

            If numberOfCores = 0 Then
                ' this only has to be assigned once
                numberOfCores = filteredInstanceNames.Count
            End If

            Dim index As Integer = 0

            Thread.Sleep(50)  ' this is absolutely needed, do not remove ( learned that the hard way after several days! ) 

            For Each s In filteredInstanceNames

                pc.InstanceName = s
                CPUValues(index) = CalculateCPUValue(cs(s), pc.NextSample)
                cs(s) = pc.NextSample

                index += 1

            Next

            ' send the readings

            ' send data

            ' byte 0 = is a '2' to represent this is a temperature and CPU data stream 
            ' byte 1 = percent of memory used whole number
            ' byte 2 = percent of memory used decimal
            ' byte 3 = average temp whole number
            ' byte 4 = average temp decimal
            ' byte 3 = max temp whole number
            ' byte 5 = max temp decimal
            ' byte 7 = number of CPUs
            ' byte 8 and on  = CPU busy of each CPU

            Dim sendArray(numberOfCores + 7) As Byte

            sendArray(0) = 2

            sendArray(1) = tempArray(0)
            sendArray(2) = tempArray(1)

            sendArray(3) = tempArray(2)
            sendArray(4) = tempArray(3)

            sendArray(5) = tempArray(4)
            sendArray(6) = tempArray(5)

            sendArray(7) = numberOfCores

            For x As Integer = 0 To numberOfCores - 1

                If CPUValues(x) > 100 Then
                    sendArray(8 + x) = 100
                ElseIf CPUValues(x) < 0 Then
                    sendArray(8 + x) = 0
                Else
                    sendArray(8 + x) = Int(Math.Round(CPUValues(x)))
                End If

            Next

            gWebSocket.Send(sendArray, 0, sendArray.Length)

        Catch ex As Exception
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & ex.ToString & vbCrLf, True)
        End Try

    End Sub

    Private Function EnsurePawnIOReadyForStartup() As Boolean

        Dim serviceController As ServiceController = Nothing

        Try
            serviceController = New ServiceController(gPawnIOServiceName)
            Dim status = serviceController.Status

            If status = ServiceControllerStatus.Running Then
                Return True
            End If

            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO is installed but not running. Attempting to start it." & vbCrLf, True)

            serviceController.Start()
            serviceController.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15))

            Dim running = (serviceController.Status = ServiceControllerStatus.Running)

            If running Then
                If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO started successfully." & vbCrLf, True)
            Else
                If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO did not reach Running state during startup." & vbCrLf, True)
            End If

            Return running

        Catch ex As InvalidOperationException
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO service is not installed. LibreHardwareMonitor will not be used." & vbCrLf, True)
            Return False
        Catch ex As Exception
            If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO startup check failed." & vbCrLf & ex.ToString & vbCrLf, True)
            Return False
        Finally
            If serviceController IsNot Nothing Then
                serviceController.Dispose()
            End If
        End Try

    End Function

    Private Function EnsurePawnIOReadyDuringRuntime() As Boolean

        Dim nowUtc As DateTime = DateTime.UtcNow

        If gLastPawnIOHealthCheckUtc <> DateTime.MinValue AndAlso nowUtc < gLastPawnIOHealthCheckUtc.AddSeconds(gPawnIOHealthCheckIntervalSeconds) Then
            Return gLastPawnIOStatusKnownGood
        End If

        gLastPawnIOHealthCheckUtc = nowUtc

        Dim serviceController As ServiceController = Nothing

        Try
            serviceController = New ServiceController(gPawnIOServiceName)
            Dim status = serviceController.Status

            If status = ServiceControllerStatus.Running Then
                gLastPawnIOStatusKnownGood = True
                Return True
            End If

            Dim shouldLogStopped As Boolean = (gLastPawnIORestartFailureLogUtc = DateTime.MinValue) OrElse (nowUtc >= gLastPawnIORestartFailureLogUtc.AddSeconds(gPawnIOLogCooldownSeconds))
            If shouldLogStopped AndAlso gDebugOn Then
                My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO is not running. Attempting restart." & vbCrLf, True)
            End If

            If gPawnIORestartWindowStartUtc = DateTime.MinValue OrElse nowUtc >= gPawnIORestartWindowStartUtc.AddMinutes(gPawnIORestartWindowMinutes) Then
                gPawnIORestartWindowStartUtc = nowUtc
                gPawnIORestartAttemptsInWindow = 0
            End If

            gPawnIORestartAttemptsInWindow += 1

            If gPawnIORestartAttemptsInWindow > gPawnIOMaxRestartAttemptsInWindow Then
                gLastPawnIOStatusKnownGood = False
                If shouldLogStopped AndAlso gDebugOn Then
                    My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO restart attempts exceeded limit in the current window." & vbCrLf, True)
                    gLastPawnIORestartFailureLogUtc = nowUtc
                End If
                Return False
            End If

            If status <> ServiceControllerStatus.StartPending Then
                serviceController.Start()
            End If

            serviceController.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15))
            gLastPawnIOStatusKnownGood = (serviceController.Status = ServiceControllerStatus.Running)

            If gLastPawnIOStatusKnownGood Then
                If gDebugOn Then My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO restarted successfully." & vbCrLf, True)
                Return True
            End If

            If shouldLogStopped AndAlso gDebugOn Then
                My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO restart attempt failed." & vbCrLf, True)
                gLastPawnIORestartFailureLogUtc = nowUtc
            End If

            Return False

        Catch ex As InvalidOperationException
            gLastPawnIOStatusKnownGood = False
            Dim shouldLogMissing As Boolean = (gLastPawnIOMissingLogUtc = DateTime.MinValue) OrElse (nowUtc >= gLastPawnIOMissingLogUtc.AddSeconds(gPawnIOLogCooldownSeconds))
            If shouldLogMissing AndAlso gDebugOn Then
                My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO service was not found during runtime. LibreHardwareMonitor is unavailable." & vbCrLf, True)
                gLastPawnIOMissingLogUtc = nowUtc
            End If
            Return False
        Catch ex As Exception
            gLastPawnIOStatusKnownGood = False
            Dim shouldLogFailure As Boolean = (gLastPawnIORestartFailureLogUtc = DateTime.MinValue) OrElse (nowUtc >= gLastPawnIORestartFailureLogUtc.AddSeconds(gPawnIOLogCooldownSeconds))
            If shouldLogFailure AndAlso gDebugOn Then
                My.Computer.FileSystem.WriteAllText(gDebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "PawnIO runtime health check failed." & vbCrLf & ex.ToString & vbCrLf, True)
                gLastPawnIORestartFailureLogUtc = nowUtc
            End If
            Return False
        Finally
            If serviceController IsNot Nothing Then
                serviceController.Dispose()
            End If
        End Try

    End Function

    Public Shared Function CalculateCPUValue(ByVal oldSample As CounterSample, ByVal newSample As CounterSample) As Double

        Dim difference As Double = (newSample.RawValue - oldSample.RawValue)
        Dim timeInterval As Double = (newSample.TimeStamp100nSec - oldSample.TimeStamp100nSec)
        If (timeInterval <> 0) Then
            Return (100 * (1 - (difference / timeInterval)))
        End If

        Return 0

    End Function

    Friend Function GetTemp() As Single

        Dim Result As Single = 0

        Dim avgTempKelvin As Single = 0

        Dim count As Integer = 0

        Try

            Dim searcher As New ManagementObjectSearcher("root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature")

            For Each queryObj As ManagementObject In searcher.Get()

                count += 1
                avgTempKelvin += queryObj("CurrentTemperature")

            Next

            If count > 0 Then

                Result = avgTempKelvin / count

                ' convert from Kelvin to Celsius

                Result = Result / 10 - 273.2  ' normally this would be 273.15 however the results from the ManagementObject only returned to one decimal place

            End If

        Catch ex As Exception

        End Try

        ' If DebugOn Then My.Computer.FileSystem.WriteAllText(DebugFilename, Now.ToLongDateString & " " & Now.ToLongTimeString & vbTab & "temperature " & Result & vbCrLf, True)

        Return Result

    End Function

End Class
Class LibreHardwareMonitor
    Public Class UpdateVisitor
        Implements IVisitor

        Public Sub VisitComputer(ByVal computer As IComputer) Implements IVisitor.VisitComputer
            computer.Traverse(Me)
        End Sub

        Public Sub VisitHardware(ByVal hardware As IHardware) Implements IVisitor.VisitHardware
            hardware.Update()

            For Each subHardware As IHardware In hardware.SubHardware
                subHardware.Accept(Me)
            Next
        End Sub
        Public Sub VisitSensor(ByVal sensor As ISensor) Implements IVisitor.VisitSensor
        End Sub

        Public Sub VisitParameter(ByVal parameter As IParameter) Implements IVisitor.VisitParameter
        End Sub
    End Class

End Class