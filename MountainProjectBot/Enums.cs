﻿
namespace MountainProjectBot
{
    public class Enums
    {
        public enum BlacklistLevel
        {
            None,               //No blacklist at all
            OnlyKeywordReplies, //Don't respond to the user's MP links. Only when the user uses the keyword !MountainProject
            NoFYI               //Same as "None", just don't use the "(FYI in the future..." line 
        }
    }
}