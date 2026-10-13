if (args[0] == "burst")
{
    for (int index = 0; index < 3000; index++)
    {
        Console.Out.WriteLine(new string('o', 1024));
        Console.Error.WriteLine(new string('e', 1024));
    }
}
else if (args[0] == "wait") await Task.Delay(TimeSpan.FromMinutes(5));
else if (args[0] == "echo") Console.Write(args[1]);
else Environment.Exit(7);
