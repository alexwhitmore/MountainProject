﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace MountainProjectAPI
{
    public static class MountainProjectDataSearch
    {
        public static List<Area> DestAreas = new List<Area>();

        public static void InitMountainProjectData(string xmlPath)
        {
            Console.WriteLine("Deserializing info from MountainProject");

            using (FileStream fileStream = new FileStream(xmlPath, FileMode.Open))
            {
                XmlSerializer xmlDeserializer = new XmlSerializer(typeof(List<Area>));
                DestAreas = (List<Area>)xmlDeserializer.Deserialize(fileStream);
            }

            if (DestAreas.Count == 0)
            {
                Console.WriteLine("Problem deserializing MountainProject info");
                Environment.Exit(13); //Invalid data
            }

            Console.WriteLine("MountainProject Info deserialized successfully");
        }

        public static MPObject SearchMountainProject(string searchText)
        {
            Console.WriteLine("Getting info from MountainProject");
            Stopwatch searchStopwatch = Stopwatch.StartNew();

            List<MPObject> results = DeepSearch(searchText, DestAreas);
            List<string> resultNames = results.Select(p => p.Name).ToList();

            Console.WriteLine($"Found {results.Count} matching results from MountainProject in {searchStopwatch.ElapsedMilliseconds} ms. Filtering by priority...");

            return FilterByPopularity(results);
        }

        public static MPObject FilterByPopularity(List<MPObject> listToFilter)
        {
            if (listToFilter.Count == 1)
                return listToFilter.First();
            if (listToFilter.Count >= 2)
            {
                List<MPObject> matchedObjectsByPopularity = listToFilter.OrderByDescending(p => p.Popularity).ToList();

                int pop1 = matchedObjectsByPopularity[0].Popularity;
                int pop2 = matchedObjectsByPopularity[1].Popularity;
                double popularityPercentDiff = Math.Round((double)(pop1 - pop2) / pop2 * 100, 2);

                Console.WriteLine($"Filtering based on priority (result has priority {popularityPercentDiff}% higher than next closest)");

                return matchedObjectsByPopularity.First(); //Return the most popular matched object (this may prioritize areas over routes. May want to check that later)
            }
            else
                return null;
        }

        private static List<MPObject> DeepSearch(string input, List<Area> destAreas)
        {
            List<MPObject> matchedObjects = new List<MPObject>();
            foreach (Area destArea in destAreas)
            {
                //If we're matching the name of a destArea (eg a State), we'll assume that the route/area is within that state
                //(eg routes named "Sweet Home Alabama") instead of considering a match on the destArea ie: Utilities.StringMatch(input, destArea.Name)

                List<MPObject> matchedSubAreas = SearchSubAreasForMatch(input, destArea.SubAreas);
                matchedObjects.AddRange(matchedSubAreas);
            }

            return matchedObjects;
        }

        private static List<MPObject> SearchSubAreasForMatch(string input, List<Area> subAreas)
        {
            List<MPObject> matchedObjects = new List<MPObject>();

            foreach (Area subDestArea in subAreas)
            {
                if (StringMatch(input, subDestArea.Name))
                    matchedObjects.Add(subDestArea);

                if (subDestArea.SubAreas != null &&
                    subDestArea.SubAreas.Count() > 0)
                {
                    List<MPObject> matchedSubAreas = SearchSubAreasForMatch(input, subDestArea.SubAreas);
                    matchedObjects.AddRange(matchedSubAreas);
                }

                if (subDestArea.Routes != null &&
                    subDestArea.Routes.Count() > 0)
                {
                    List<MPObject> matchedRoutes = SearchRoutes(input, subDestArea.Routes);
                    matchedObjects.AddRange(matchedRoutes);
                }
            }

            return matchedObjects;
        }

        private static List<MPObject> SearchRoutes(string input, List<Route> routes)
        {
            List<MPObject> matchedObjects = new List<MPObject>();

            foreach (Route route in routes)
            {
                if (StringMatch(input, route.Name))
                    matchedObjects.Add(route);
            }

            return matchedObjects;
        }

        public static bool StringMatch(string inputString, string targetString, bool caseInsensitive = true)
        {
            //Match regardless of # of spaces
            string input = inputString.Replace(" ", "");
            string target = targetString.Replace(" ", "");

            if (caseInsensitive)
            {
                input = input.ToLower();
                target = target.ToLower();
            }

            return target.Contains(input);
        }

        public static MPObject GetItemWithMatchingUrl(string url, List<MPObject> listToSearch)
        {
            foreach (MPObject item in listToSearch)
            {
                if (item.URL == url)
                    return item;
                else
                {
                    if (item is Area)
                    {
                        MPObject matchingRoute = (item as Area).Routes.Find(p => p.URL == url);
                        if (matchingRoute != null)
                            return matchingRoute;
                        else
                        {
                            MPObject matchingSubArea = GetItemWithMatchingUrl(url, (item as Area).SubAreas.Cast<MPObject>().ToList());
                            if (matchingSubArea != null)
                                return matchingSubArea;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the parent for the child (ie Area that contains the object)
        /// </summary>
        /// <param name="child">The object of which to get the parent</param>
        /// <param name="parentLevel">The parent level. "0" is the highest parent, "1" is the next parent down, etc. "-1" will return the immediate parent</param>
        /// <returns>The parent object to the child. Will return null if the child has no parents or if the parentLevel is invalid</returns>
        public static MPObject GetParent(MPObject child, int parentLevel)
        {
            string url;
            if (parentLevel == -1)
                url = child.ParentUrls.Last();
            else if (parentLevel > child.ParentUrls.Count - 1) //Out of range
                return null;
            else
                url = child.ParentUrls[parentLevel];

            return GetItemWithMatchingUrl(url, DestAreas.Cast<MPObject>().ToList());
        }
    }
}