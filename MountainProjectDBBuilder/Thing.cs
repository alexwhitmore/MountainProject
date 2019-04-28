﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MountainProjectDBBuilder
{
    public class Thing
    {
        public Thing(string name, string url)
        {
            this.Name = WebUtility.HtmlDecode(name);
            this.URL = url;
        }

        public Thing()
        {

        }

        private string name { get; set; }
        public string Name
        {
            get
            {
                return name;
            }
            set
            {
                if (Regex.Match(value, ", The", RegexOptions.IgnoreCase).Success)
                {
                    value = Regex.Replace(value, ", The", "", RegexOptions.IgnoreCase);
                    value = "The " + value;
                }

                name = value.Trim();
            }
        }
        public string URL { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}