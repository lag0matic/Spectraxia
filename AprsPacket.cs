using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Spectraxia;

internal sealed record AprsPacket(string Source, string Destination, string Path, string Payload,
    string Kind, string Summary, GeoPosition? Position)
{
    internal static AprsPacket? Parse(byte[] data)
    {
        List<string> addresses = [];
        int offset = 0;
        bool last = false;
        while (!last && addresses.Count < 10 && offset+7 <= data.Length)
        {
            var chars = new char[6];
            for (int i=0;i<6;i++)
            {
                byte b = data[offset+i];
                char c = (char)(b>>1);
                if ((b&1)!=0 || !(char.IsAsciiLetterOrDigit(c) || c==' ')) return null;
                chars[i] = c;
            }
            string call = new string(chars).TrimEnd();
            if (call.Length == 0 || call.Contains(' ')) return null;
            byte flags = data[offset+6];
            int ssid = (flags>>1)&15;
            if (ssid != 0) call += $"-{ssid}";
            if (addresses.Count>=2 && (flags&128)!=0) call += "*";
            addresses.Add(call);
            last = (flags&1)!=0;
            offset+=7;
        }
        if (!last || addresses.Count<2 || offset+2>=data.Length || data[offset]!=3 || data[offset+1]!=0xf0) return null;
        var payload = Encoding.Latin1.GetString(data,offset+2,data.Length-offset-2).TrimEnd('\r','\n');
        if (payload.Length==0) return null;
        return Interpret(addresses[1],addresses[0],string.Join(",",addresses.Skip(2)),payload);
    }

    internal static AprsPacket Interpret(string source,string dest,string path,string info)
    {
        GeoPosition? fix = null;
        string kind="Packet", summary=info;
        if (info.Length>0 && "!=/@".Contains(info[0]))
        {
            int start = info[0] is '/' or '@' ? 8 : 1;
            if (info.Length>start && TryPosition(info[start..],out var pos,out char symbol,out string comment))
            {
                fix=pos;
                kind=symbol=='_' ? "Weather" : "Position";
                summary=symbol=='_' ? Weather(comment) : comment;
            }
        }
        else if (info.Length>=9 && info[0] is '\'' or (char)96 && MicE(dest,info) is {} mic)
        { fix=mic; kind="Mic-E"; summary=info[9..]; }
        else if (info.Length>=11 && info[0]==':' && info[10]==':')
        { kind="Message"; summary=$"To {info[1..10].TrimEnd()} · {info[11..]}"; }
        else if (info.StartsWith('_'))
        { kind="Weather"; summary=Weather(info[1..]); }
        else if (info.StartsWith('>')) { kind="Status"; summary=info[1..]; }
        else if (info.StartsWith("T#")) kind="Telemetry";
        else if (info.StartsWith('}')) kind="Third-party";
        else if (info.StartsWith(';')) kind="Object";
        else if (info.StartsWith(')')) kind="Item";
        return new(source,dest,path,info,kind,summary,fix);
    }

    private static string Weather(string data)
    {
        List<string> fields=[];
        var wind = Regex.Match(data,@"^(\d{3})/(\d{3})");
        if (wind.Success) fields.Add($"wind {int.Parse(wind.Groups[2].Value)} mph from {wind.Groups[1].Value}°");
        foreach (Match m in Regex.Matches(data,@"(?<key>[tgshb])(?<v>-?\d+)"))
        {
            if (!int.TryParse(m.Groups["v"].Value,out int v)) continue;
            string? field=m.Groups["key"].Value switch {
                "t" => $"{v} °F", "h" => $"{(v==0?100:v)}% RH", "b" => $"{v/10.0:0.0} hPa",
                "g" => $"gust {v} mph", "s" => $"wind {v} mph", _ => null };
            if(field!=null) fields.Add(field);
        }
        return fields.Count==0 ? data : string.Join(" · ",fields);
    }
    private static bool TryPosition(string s,out GeoPosition pos,out char symbol,out string comment)
    {
        pos=default; symbol=' '; comment="";
        if (s.Length>=19 && s[7] is 'N' or 'S' && s[17] is 'E' or 'W')
        {
            static bool Angle(string field,int digits,out double value)
            {
                value=0;
                if (field[digits+2]!='.' || !field[..digits].All(char.IsAsciiDigit) ||
                    !double.TryParse(field[digits..],NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out double min) || min>=60) return false;
                value=int.Parse(field[..digits],CultureInfo.InvariantCulture)+min/60;
                return true;
            }
            if (!Angle(s[..7],2,out double lat) || !Angle(s[9..17],3,out double lon) || lat>90 || lon>180) return false;
            pos=new(s[7]=='S'?-lat:lat,s[17]=='W'?-lon:lon);
            symbol=s[18]; comment=s[19..]; return true;
        }
        if (s.Length<13 || !(s[0] is '/' or '\\' || char.IsAsciiLetter(s[0])) || !s[1..9].All(c=>c is >= '!' and <= '{')) return false;
        int Base91(int start) {int n=0; for(int i=start;i<start+4;i++) n=n*91+s[i]-33; return n;}
        double y=90-Base91(1)/380926.0,x=-180+Base91(5)/190463.0;
        if (Math.Abs(y)>90 || Math.Abs(x)>180) return false;
        pos=new(y,x);symbol=s[9];comment=s[13..];return true;
    }
    private static GeoPosition? MicE(string dest,string data)
    {
        var call=dest.Split('-')[0];
        if (call.Length!=6 || data[1..4].Any(c=>c<28 || c>127)) return null;
        int digits=0;
        foreach(char c in call)
        {
            int d=c switch { >= '0' and <= '9'=>c-'0', >= 'A' and <= 'J'=>c-'A', >= 'P' and <= 'Y'=>c-'P', _=>-1 };
            if(d<0) return null;
            digits=digits*10+d;
        }
        int deg=data[1]-28+(call[4]>='P'?100:0);
        if(deg>=180 && deg<=189) deg-=80; else if(deg>=190 && deg<=199) deg-=190;
        int min=data[2]-28; if(min>=60) min-=60;
        int hundredths=data[3]-28;
        if(digits%10000>=6000 || min>=60 || hundredths>=100) return null;
        double lat=digits/10000+(digits%10000)/6000.0,lon=deg+(min+hundredths/100.0)/60;
        if(lat>90 || lon>180) return null;
        return new(call[3]>='P'?lat:-lat,call[5]>='P'?-lon:lon);
    }
}
