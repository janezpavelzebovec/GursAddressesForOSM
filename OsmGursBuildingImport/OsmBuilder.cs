using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using OsmSharp;
using OsmSharp.Complete;
using OsmSharp.Tags;

namespace OsmGursBuildingImport
{
    class OsmBuilder
    {
        int newIdCounter = -1;
        List<ICompleteOsmGeo> geos = new();
        Dictionary<Coordinate, Node> ExistingNodes = new();


        private ICompleteOsmGeo LineStringToWay(LineString lineString)
        {
            var nodes = new List<Node>();
            foreach (var coord in lineString.Coordinates)
            {
                nodes.Add((Node)GeometryToOsmGeo(new Point(coord)));
            }

            return Add(new CompleteWay {
                Id = newIdCounter--,
                Nodes = nodes.ToArray()
            });
        }

        public bool UpdateBuilding(ICompleteOsmGeo building, BuildingInfo gursBuilding, bool setAddressOnBuilding)
        {
            var attributes = building.Tags;
            var anythingUpdated = UpdateAttribute(attributes, "ref:gurs:sta_sid", gursBuilding.Id.ToString());
            
            // SPREMENJENO ZA OHM: VEDNO ustvari naslove kot ločena vozlišča
            var addresses = gursBuilding.Addresses;
            if (addresses != null && addresses.Count > 0)
            {
                // Za OpenHistoricalMap: vsi naslovi so ločena vozlišča z lastnim start_date
                foreach (var addr in addresses)
                {
                    CreateNewNodeFromAddress(addr);
                }
            }

            return anythingUpdated;
        }

        public void CreateNewNodeFromAddress(Address addr)
        {
            var newNode = GetOrCreateNode(addr.Geometry.Centroid);
            newNode.Tags = new TagsCollection();
            SetAddressAttributes(addr, newNode.Tags);
        }

        private static IEnumerable<ICompleteOsmGeo> GetNodes(ICompleteOsmGeo building)
        {
            switch (building)
            {
                case Node node:
                    yield return node;
                    break;
                case CompleteWay way:
                    foreach (var node in way.Nodes)
                    {
                        yield return node;
                    }
                    break;
                case CompleteRelation relation:
                    foreach (var member in relation.Members)
                    {
                        foreach (var item in GetNodes(member.Member))
                        {
                            yield return item;
                        }
                    }
                    break;
            }
        }

        private static bool UpdateAttribute(TagsCollectionBase attributes, string attributeName, string newValue)
        {
            if (attributes.TryGetValue(attributeName, out var oldValue))
            {
                if (oldValue == newValue)
                {
                    return false;
                }
                if (attributeName == "addr:housenumber" && attributes[attributeName].Equals(newValue, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                AddFixmeAttribute(attributes,
                    $"\"{attributeName}\" changed from {attributes[attributeName]} to {newValue}.");

                attributes[attributeName] = newValue;
                return true;
            }
            else
            {
                attributes[attributeName] = newValue;
                return true;
            }
        }

        public static void AddFixmeAttribute(TagsCollectionBase attributes, string fixmeMessage)
        {
            if (attributes.ContainsKey("fixme"))
            {
                for (int i = 1; i < 100; i++)
                {
                    if (!attributes.ContainsKey("fixme" + i))
                    {
                        attributes["fixme" + i] = fixmeMessage;
                        return;
                    }
                }
                throw new Exception("What is happening? More than 100 fixmes...");
            }

            attributes["fixme"] = fixmeMessage;
        }

        private static string Suffix(Coordinate coordinate)
        {
            return coordinate.X > 14.815333 ? ":hu" : ":it";
        }

        public static bool SetAddressAttributes(Address address, TagsCollectionBase attributes)
        {
            var anythingWasSet = false;
            anythingWasSet |= UpdateAttribute(attributes, "addr:housenumber", address.HouseNumber);
            if (string.IsNullOrEmpty(address.StreetName.NameSecondLanguage))
            {
                anythingWasSet |= UpdateAttribute(attributes, "addr:street", address.StreetName.Name);
            }
            else
            {
                anythingWasSet |= UpdateAttribute(attributes, "addr:street", address.StreetName.Name + " / " + address.StreetName.NameSecondLanguage);
                anythingWasSet |= UpdateAttribute(attributes, "addr:street:sl", address.StreetName.Name);
                anythingWasSet |= UpdateAttribute(attributes, "addr:street" + Suffix(address.Geometry.Coordinate), address.StreetName.NameSecondLanguage);
            }

            if (string.IsNullOrEmpty(address.PostInfo.Name.NameSecondLanguage))
            {
                anythingWasSet |= UpdateAttribute(attributes, "addr:city", address.PostInfo.Name.Name);
            }
            else
            {
                anythingWasSet |= UpdateAttribute(attributes, "addr:city", address.PostInfo.Name.Name + " / " + address.PostInfo.Name.NameSecondLanguage);
                anythingWasSet |= UpdateAttribute(attributes, "addr:city:sl", address.PostInfo.Name.Name);
                anythingWasSet |= UpdateAttribute(attributes, "addr:city" + Suffix(address.Geometry.Coordinate), address.PostInfo.Name.NameSecondLanguage);
            }

            anythingWasSet |= UpdateAttribute(attributes, "addr:postcode", address.PostInfo.Id.ToString());

            if (!address.PostInfo.Name.Name.StartsWith(address.VillageName.Name) &&
                address.StreetName.Name != address.VillageName.Name)
            {
                if (string.IsNullOrEmpty(address.VillageName.NameSecondLanguage))
                {
                    anythingWasSet |= UpdateAttribute(attributes, "addr:village", address.VillageName.Name);
                }
                else
                {
                    anythingWasSet |= UpdateAttribute(attributes, "addr:village", address.VillageName.Name + " / " + address.VillageName.NameSecondLanguage);
                    anythingWasSet |= UpdateAttribute(attributes, "addr:village:sl", address.VillageName.Name);
                    anythingWasSet |= UpdateAttribute(attributes, "addr:village" + Suffix(address.Geometry.Coordinate), address.VillageName.NameSecondLanguage);
                }
            }

            if (anythingWasSet)
            {
                attributes.RemoveAll(t => t.Key.StartsWith("addr:place"));
                attributes.RemoveAll(t => t.Key.StartsWith("addr:hamlet"));
            }

            if (anythingWasSet)
            {
                // SPREMENJENO ZA OHM: addr:source namesto source:addr
                UpdateAttribute(attributes, "addr:source", "GURS");
                if (!string.IsNullOrEmpty(address.Date))
                    UpdateAttribute(attributes, "addr:source:date", address.Date);
                
                // SPREMENJENO ZA OHM: start_date logika za naslove
                // start_date = datum zadnje posodobitve (ali trenutni datum)
                var lastUpdateDate = !string.IsNullOrEmpty(address.Date) 
                    ? address.Date 
                    : DateTime.Now.ToString("yyyy-MM-dd");
                
                UpdateAttribute(attributes, "start_date", lastUpdateDate);
                
                // start_date:edtf = obdobje med izgradnjo zgradbe in zadnjo posodobitvijo
                if (address.BuildingConstructionYear.HasValue)
                {
                    // Naslov je bil dodan med izgradnjo in zadnjo posodobitvijo
                    UpdateAttribute(attributes, "start_date:edtf", $"{address.BuildingConstructionYear}/{lastUpdateDate}");
                }
                else
                {
                    // Če ni letnice zgradbe, naslov obstaja že pred zadnjo posodobitvijo
                    UpdateAttribute(attributes, "start_date:edtf", $"/{lastUpdateDate}");
                }
            }
            
            return UpdateAttribute(attributes, "ref:gurs:hs_mid", address.Id.ToString()) | anythingWasSet;
        }

        internal void AddBuilding(BuildingInfo gursBuilding, bool setAddressOnBuilding)
        {
            var newBuilding = GeometryToOsmGeo(gursBuilding.Geometry);
            newBuilding.Tags ??= new TagsCollection();
            newBuilding.Tags.Add("building", "yes");
            
            // SPREMENJENO ZA OHM: source in source:date za zgradbe
            newBuilding.Tags.Add("source", "GURS");
            if (!string.IsNullOrEmpty(gursBuilding.Date))
                newBuilding.Tags.Add(new Tag("source:date", gursBuilding.Date));
            
            // SPREMENJENO ZA OHM: construction_date → start_date
            if (gursBuilding.ConstructionYear.HasValue)
            {
                newBuilding.Tags.Add(new Tag("start_date", gursBuilding.ConstructionYear.ToString()));
                newBuilding.Tags.Add(new Tag("start_date:source", "GURS"));
            }
            else
            {
                // Za zgradbe brez letnice - uporabi poln datum iz GURS ali trenutni datum
                if (!string.IsNullOrEmpty(gursBuilding.Date))
                {
                    // Uporabi poln datum iz GURS (format: YYYY-MM-DD)
                    newBuilding.Tags.Add(new Tag("start_date", gursBuilding.Date));
                    newBuilding.Tags.Add(new Tag("start_date:edtf", $"/{gursBuilding.Date}"));
                }
                else
                {
                    // Če ni datuma, uporabi trenutni datum
                    var currentDate = DateTime.Now.ToString("yyyy-MM-dd");
                    newBuilding.Tags.Add(new Tag("start_date", currentDate));
                    newBuilding.Tags.Add(new Tag("start_date:edtf", $"/{currentDate}"));
                }
            }

            // DODANO: Dodatni atributi iz GURS
            
            // Višina zgradbe = H2 - H3 (od karakteristične višine do vrha)
            if (gursBuilding.Elevation.HasValue && gursBuilding.MaxElevation.HasValue)
            {
                var buildingHeight = gursBuilding.MaxElevation.Value - gursBuilding.Elevation.Value;
                if (buildingHeight > 0)
                {
                    newBuilding.Tags.Add(new Tag("height", buildingHeight.ToString("F1")));
                }
            }

            // Število nadstropij
            if (gursBuilding.Levels.HasValue && gursBuilding.Levels.Value > 0)
            {
                newBuilding.Tags.Add(new Tag("building:levels", gursBuilding.Levels.Value.ToString()));
            }

            // Nadmorska višina (H3 - karakteristična višina, pritličje/vhod)
            if (gursBuilding.Elevation.HasValue)
            {
                newBuilding.Tags.Add(new Tag("ele", gursBuilding.Elevation.Value.ToString("F1")));
            }

            // Material nosilne konstrukcije
            if (!string.IsNullOrEmpty(gursBuilding.Material))
            {
                newBuilding.Tags.Add(new Tag("building:material", gursBuilding.Material));
            }

            // Tip položaja zgradbe (lahko uporabimo za namig building=*)
            if (!string.IsNullOrEmpty(gursBuilding.BuildingType))
            {
                // Za referenco shranimo GURS tip
                newBuilding.Tags.Add(new Tag("building:type:gurs", gursBuilding.BuildingType));
            }

            UpdateBuilding(newBuilding, gursBuilding, setAddressOnBuilding);
        }

        public ICompleteOsmGeo GeometryToOsmGeo(Geometry geometry)
        {
            switch (geometry)
            {
                case Point point:
                    return GetOrCreateNode(point);
                case Polygon polygon:
                    {
                        if (polygon.NumInteriorRings == 0)
                        {
                            return LineStringToWay(polygon.Shell);
                        }
                        else
                        {
                            var members = new List<CompleteRelationMember>();
                            var outerWay = LineStringToWay(polygon.Shell);
                            members.Add(new CompleteRelationMember() {
                                Member = outerWay,
                                Role = "outer"
                            });
                            foreach (var pol in polygon.InteriorRings)
                            {
                                var osmGeo = LineStringToWay(pol);
                                members.Add(new CompleteRelationMember {
                                    Member = osmGeo,
                                    Role = "inner"
                                });
                            }

                            return Add(new CompleteRelation() {
                                Id = newIdCounter--,
                                Members = members.ToArray(),
                                Tags = new TagsCollection()
                                {
                                    { "type", "multipolygon" }
                                }
                            });
                        }
                    }
                case MultiPolygon multiPolygon:
                    {
                        var members = new List<CompleteRelationMember>();
                        foreach (var pol in multiPolygon.Geometries)
                        {
                            var osmGeo = GeometryToOsmGeo(pol);
                            members.Add(new CompleteRelationMember() {
                                Member = osmGeo,
                                Role = "outer"
                            });
                        }

                        return Add(new CompleteRelation() {
                            Id = newIdCounter--,
                            Members = members.ToArray(),
                            Tags = new TagsCollection()
                                {
                                    { "type", "multipolygon" }
                                }
                        });
                    }
                default:
                    throw new NotImplementedException(geometry.GetType().FullName);
            }
        }

        public Node GetOrCreateNode(Point point)
        {
            if (ExistingNodes.TryGetValue(point.Coordinate, out var node))
                return node;
            var newNode = new Node() {
                Id = newIdCounter--,
                Longitude = point.X,
                Latitude = point.Y
            };
            ExistingNodes.Add(point.Coordinate, newNode);
            return Add(newNode);
        }

        private T Add<T>(T geo) where T : ICompleteOsmGeo
        {
            geos.Add(geo);
            return geo;
        }

        public IEnumerable<ICompleteOsmGeo> GetGeos()
        {
            return geos;
        }
    }
}
